using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FiatCryptoGateway.Application.Compliance;
using FiatCryptoGateway.Application.OnRamp;
using FiatCryptoGateway.Application.Ports;
using FiatCryptoGateway.Application.Reconciliation;
using FiatCryptoGateway.Application.Settlement;
using FiatCryptoGateway.Domain.Compliance;
using FiatCryptoGateway.Domain.Settlement;
using FiatCryptoGateway.Domain.Assets;
using FiatCryptoGateway.Domain.Ledger;
using FiatCryptoGateway.Domain.Orders;
using FiatCryptoGateway.Persistence.Postgres;
using FiatCryptoGateway.Sandbox;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FiatCryptoGateway.Persistence.Tests;

[Collection(SharedPostgres.Name)]
public sealed partial class PostgresStoreTests(PostgresFixture fixture)
{
    private const string Client = PostgresFixture.ClientId;
    private const string Address = "0x1111111111111111111111111111111111111111";
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly DateTimeOffset Start = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Migrations_AreAppliedOnceAndRerunsChangeNothing()
    {
        await using var services = fixture.BuildServices(TimeProvider.System);

        Assert.Equal(0, await services.GetRequiredService<PostgresMigrator>().ApplyAsync(None));

        await using var connection = await services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT string_agg(version, ',' ORDER BY version) FROM ops.schema_migration", connection);
        Assert.Equal(string.Join(',', MigrationFileVersions()), await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task CompletedOrder_IsIdenticalWhenReloadedByAnotherInstance()
    {
        var time = new ManualTimeProvider(Start);
        await using var first = fixture.BuildServices(time);
        var merchant = await MerchantWithWalletAsync(first);
        var onRamp = first.GetRequiredService<OnRampService>();
        var quote = await onRamp.CreateQuoteAsync(merchant, new CreateQuoteCommand("INR", 10_000m, "USDC@ethereum"), None);
        var order = await onRamp.CreateOrderAsync(merchant, new CreateOrderCommand(quote.QuoteId, SandboxCustomerDirectory.Verified), None);
        Assert.Equal(Address, order.DestinationAddress);

        var webhooks = first.GetRequiredService<SandboxFiatWebhookHandler>();
        var (body, signature) = webhooks.CreateSignedPaymentEvent(order);
        Assert.Equal(WebhookResult.Accepted, await webhooks.HandleAsync(body, signature, None));
        var sent = await onRamp.GetOrderAsync(merchant, order.OrderId, None);
        await onRamp.RecordConfirmationsAsync(merchant, order.OrderId, sent.Transfer!.TransactionHash, 12, None);

        // A second instance shares nothing with the first except the database.
        await using var second = fixture.BuildServices(time);
        var reloaded = await second.GetRequiredService<OnRampService>().GetOrderAsync(merchant, order.OrderId, None);

        Assert.Equal(OrderStatus.Completed, reloaded.Status);
        Assert.Equal(
            ["ORDER_CREATED", "FIAT_CAPTURED", "COMPLIANCE_APPROVED", "CRYPTO_PURCHASED", "CRYPTO_BROADCAST", "CHAIN_CONFIRMED"],
            reloaded.Events.Select(e => e.Reason));
        Assert.Equal(2, reloaded.ComplianceHistory.Count); // before payment and after payment
        Assert.Equal(quote.MarketRate, reloaded.Quote.MarketRate);
        Assert.Equal(quote.CustomerRate, reloaded.Quote.CustomerRate);
        Assert.Equal("114.148850", reloaded.Quote.DestinationAmount.ToFixedString());
        Assert.Equal(quote.PricePath.Count, reloaded.Quote.PricePath.Count);
        Assert.Equal(sent.Transfer.TransactionHash, reloaded.Transfer!.TransactionHash);
        Assert.Equal(order.Quote.SourceAmount, reloaded.FiatPayment!.Amount);
        Assert.Equal(12, reloaded.Confirmations);

        var entries = await second.GetRequiredService<ILedgerStore>().ListAsync(order.OrderId, None);
        Assert.Equal(["FIAT_RECEIVED", "CRYPTO_PURCHASED", "CRYPTO_SENT"], entries.Select(entry => entry.Kind));
        Assert.Equal(
            new ExternalReference(SandboxFiatProvider.ProviderName, reloaded.FiatPayment.ProviderPaymentId, reloaded.FiatPayment.Amount, reloaded.FiatPayment.CapturedAt),
            entries[0].External);
        Assert.Equal(
            new ExternalReference(reloaded.Transfer.Provider, reloaded.Transfer.TransactionHash, reloaded.Transfer.Amount, reloaded.Transfer.BroadcastAt),
            entries[2].External);
        var balances = LedgerBalances.Compute(entries);
        Assert.All(balances.GroupBy(balance => balance.Asset), asset => Assert.Equal(0m, asset.Sum(balance => balance.DebitMinusCredit)));
        Assert.All(
            balances.Where(balance => balance.Account == "liabilities:customer_orders"),
            balance => Assert.Equal(0m, balance.DebitMinusCredit));
    }

    [Fact]
    public async Task TwoInstances_RacingOnOneOrder_BuyAndSendOnlyOnce()
    {
        var time = new ManualTimeProvider(Start);
        await using var instanceA = fixture.BuildServices(time);
        await using var instanceB = fixture.BuildServices(time);
        var order = await CreatePaidButUnprocessedOrderAsync(instanceA, time);

        await Task.WhenAll(
            instanceA.GetRequiredService<OnRampService>().ProcessAsync(order.OrderId, None),
            instanceB.GetRequiredService<OnRampService>().ProcessAsync(order.OrderId, None));

        // Each instance has its own sandbox providers, so only the cross-instance lock prevents a double buy.
        var purchases = instanceA.GetRequiredService<SandboxLiquidityProvider>().ExecutionCount
            + instanceB.GetRequiredService<SandboxLiquidityProvider>().ExecutionCount;
        var transfers = instanceA.GetRequiredService<SandboxCustodyProvider>().TransferCount
            + instanceB.GetRequiredService<SandboxCustodyProvider>().TransferCount;
        Assert.Equal(1, purchases);
        Assert.Equal(1, transfers);

        var entries = await instanceA.GetRequiredService<ILedgerStore>().ListAsync(order.OrderId, None);
        Assert.Equal(["FIAT_RECEIVED", "CRYPTO_PURCHASED", "CRYPTO_SENT"], entries.Select(entry => entry.Kind));
        var final = await instanceB.GetRequiredService<IOrderStore>().FindAsync(order.OrderId, None);
        Assert.Equal(OrderStatus.CryptoSent, final!.Status);
    }

    [Fact]
    public async Task StaleCopy_CannotOverwriteNewerState()
    {
        var time = new ManualTimeProvider(Start);
        await using var services = fixture.BuildServices(time);
        var created = await CreateOrderAsync(services);
        var store = services.GetRequiredService<IOrderStore>();
        var first = (await store.FindAsync(created.OrderId, None))!;
        var second = (await store.FindAsync(created.OrderId, None))!;

        Assert.True(first.ExpireIfOverdue(first.PaymentDeadline.AddSeconds(1)));
        await store.SaveAsync(first, [], None);
        Assert.True(second.ExpireIfOverdue(second.PaymentDeadline.AddSeconds(1)));

        await Assert.ThrowsAsync<OrderConcurrencyException>(() => store.SaveAsync(second, [], None));
    }

    [Fact]
    public async Task ProviderPayment_CannotBeBookedTwice()
    {
        var time = new ManualTimeProvider(Start);
        await using var services = fixture.BuildServices(time);
        var order = await CreatePaidButUnprocessedOrderAsync(services, time);
        var payment = order.FiatPayment!;

        var duplicate = OnRampPostings.FiatReceived(order, payment, time.GetUtcNow());
        var exception = await Assert.ThrowsAsync<PostgresException>(() => services.GetRequiredService<ILedgerStore>().AppendAsync(duplicate, None));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, exception.SqlState);
        Assert.Single(await services.GetRequiredService<ILedgerStore>().ListAsync(order.OrderId, None));
    }

    [Fact]
    public async Task IdempotencyKeys_AreClaimedOnceAndReplayedByteForByte()
    {
        await using var services = fixture.BuildServices(TimeProvider.System);
        var store = services.GetRequiredService<IIdempotencyStore>();
        var key = Guid.NewGuid().ToString();
        var hash = new string('a', 64);
        const string body = """{"orderId":"9f0c","status":"AwaitingFiatPayment"}""";

        Assert.Null(await store.TryClaimAsync(Client, key, hash, None));
        Assert.Null((await store.TryClaimAsync(Client, key, hash, None))!.Response); // still in progress
        await store.CompleteAsync(Client, key, new StoredResponse(201, body), None);

        var replay = await store.TryClaimAsync(Client, key, hash, None);
        Assert.Equal(201, replay!.Response!.StatusCode);
        Assert.Equal(body, replay.Response.Body);

        var released = Guid.NewGuid().ToString();
        Assert.Null(await store.TryClaimAsync(Client, released, hash, None));
        await store.ReleaseAsync(Client, released, None);
        Assert.Null(await store.TryClaimAsync(Client, released, hash, None));
    }

    [Fact]
    public async Task WebhookEvents_AreClaimedOnce()
    {
        await using var services = fixture.BuildServices(TimeProvider.System);
        var inbox = services.GetRequiredService<IWebhookInbox>();
        var eventId = $"evt_{Guid.NewGuid():N}";
        var hash = new string('b', 64);

        Assert.True(await inbox.TryClaimAsync("sandbox-fiat", eventId, hash, None));
        Assert.False(await inbox.TryClaimAsync("sandbox-fiat", eventId, hash, None));
        await inbox.ReleaseAsync("sandbox-fiat", eventId, None);
        Assert.True(await inbox.TryClaimAsync("sandbox-fiat", eventId, hash, None));
    }

    [Fact]
    public async Task CustomerVolume_CountsRecentLiveOrdersOnly()
    {
        var customer = $"cust_{Guid.NewGuid():N}";
        var time = new ManualTimeProvider(Start);
        await using var services = fixture.BuildServices(time);
        var store = services.GetRequiredService<IOrderStore>();

        var old = await AddOrderAsync(services, customer, time);          // 2 days ago: in the month, not the day
        time.Advance(TimeSpan.FromDays(2));
        var recent = await AddOrderAsync(services, customer, time);
        var expired = await AddOrderAsync(services, customer, time);
        var excluded = await AddOrderAsync(services, customer, time);      // the order being screened
        Assert.True(expired.ExpireIfOverdue(expired.PaymentDeadline.AddSeconds(1)));
        await store.SaveAsync(expired, [], None);
        var now = time.GetUtcNow();

        var volume = await store.GetCustomerVolumeAsync(
            Client, customer, old.Quote.PivotAmount.Asset.Id, now.AddHours(-24), now.AddDays(-30), excluded.OrderId, None);

        var each = recent.Quote.PivotAmount.Amount;
        Assert.Equal(each, volume.LastDay);        // only "recent"
        Assert.Equal(2 * each, volume.LastMonth);  // "recent" and "old"
    }

    [Fact]
    public async Task SimultaneousOrders_CannotTogetherExceedADailyLimit()
    {
        var time = new ManualTimeProvider(Start);
        await using var instanceA = fixture.BuildServices(time);
        await using var instanceB = fixture.BuildServices(time);
        var merchant = await MerchantWithWalletAsync(instanceA);

        // Basic KYC allows 2,000 USD per day: three 900 USD orders arrive at once on two instances.
        var results = await Task.WhenAll(new[] { instanceA, instanceB, instanceA }.Select(async services =>
        {
            var onRamp = services.GetRequiredService<OnRampService>();
            var quote = await onRamp.CreateQuoteAsync(merchant, new CreateQuoteCommand("USD", 900m, "USDC@ethereum"), None);
            try
            {
                await onRamp.CreateOrderAsync(merchant, new CreateOrderCommand(quote.QuoteId, SandboxCustomerDirectory.BasicKyc), None);
                return true;
            }
            catch (FiatCryptoGateway.Domain.GatewayRuleException exception) when (exception.Code == "DAILY_LIMIT_EXCEEDED")
            {
                return false;
            }
        }));

        Assert.Equal(2, results.Count(created => created));
    }

    [Fact]
    public async Task DatabaseStateMachine_MatchesTheCode()
    {
        await using var services = fixture.BuildServices(TimeProvider.System);
        await using var connection = await services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync();

        var statuses = new Dictionary<string, bool>(StringComparer.Ordinal);
        await using (var command = new NpgsqlCommand("SELECT status, is_terminal FROM gateway.order_status", connection))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                statuses[reader.GetString(0)] = reader.GetBoolean(1);
            }
        }

        var transitions = new List<string>();
        await using (var command = new NpgsqlCommand("SELECT from_status || '->' || to_status FROM gateway.order_transition", connection))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                transitions.Add(reader.GetString(0));
            }
        }

        var all = Enum.GetValues<OrderStatus>();
        var expectedTransitions = all
            .SelectMany(from => all.Where(to => PaymentOrder.IsAllowedTransition(from, to)).Select(to => $"{DbName(from)}->{DbName(to)}"))
            .Order(StringComparer.Ordinal);
        var expectedTerminal = all
            .Where(from => !all.Any(to => PaymentOrder.IsAllowedTransition(from, to)))
            .Select(DbName)
            .Order(StringComparer.Ordinal);

        Assert.Equal(all.Select(DbName).Order(StringComparer.Ordinal), statuses.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(expectedTransitions, transitions.Order(StringComparer.Ordinal));
        Assert.Equal(expectedTerminal, statuses.Where(pair => pair.Value).Select(pair => pair.Key).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ReviewActions_AreKeptAndReloaded()
    {
        var time = new ManualTimeProvider(Start);
        await using var first = fixture.BuildServices(time);
        var order = await CreateOrderAsync(first, SandboxWalletScreening.HighRiskAddress);
        var webhooks = first.GetRequiredService<SandboxFiatWebhookHandler>();
        var (body, signature) = webhooks.CreateSignedPaymentEvent(order);
        await webhooks.HandleAsync(body, signature, None);

        var reviews = first.GetRequiredService<ComplianceReviewService>();
        Assert.Contains(await reviews.GetQueueAsync(None), item => item.Order.OrderId == order.OrderId);
        await reviews.ReviewAsync(order.OrderId, ReviewActionKind.Reject, "officer", "Wallet linked to a mixer.", null, None);
        await reviews.ReviewAsync(order.OrderId, ReviewActionKind.FreezeAndReport, "officer", "Reported.", "STR-2026-0042", None);

        await using var second = fixture.BuildServices(time);
        var reloaded = await second.GetRequiredService<ComplianceReviewService>().GetOrderAsync(order.OrderId, None);

        Assert.Equal(OrderStatus.Frozen, reloaded.Status);
        Assert.Equal("STR-2026-0042", reloaded.ReportReference);
        Assert.Equal(
            [(ReviewActionKind.Reject, "officer", "Wallet linked to a mixer."), (ReviewActionKind.FreezeAndReport, "officer", "Reported.")],
            reloaded.ReviewActions.Select(action => (action.Kind, action.Reviewer, action.Reason)));
        Assert.DoesNotContain(await reviews.GetQueueAsync(None), item => item.Order.OrderId == order.OrderId);
    }

    [Fact]
    public async Task MerchantNotifications_AreSavedInTheSameTransactionAsTheChange()
    {
        var time = new ManualTimeProvider(Start);
        await using var services = fixture.BuildServices(time);
        var created = await CreateOrderAsync(services);
        var outbox = services.GetRequiredService<IOutbox>();
        var store = services.GetRequiredService<IOrderStore>();
        Assert.Equal([1], (await outbox.ListForOrderAsync(created.OrderId, None)).Select(status => status.Sequence));

        var current = (await store.FindAsync(created.OrderId, None))!;
        var stale = (await store.FindAsync(created.OrderId, None))!;
        Assert.True(current.ExpireIfOverdue(current.PaymentDeadline.AddSeconds(1)));
        await store.SaveAsync(current, [], None);
        Assert.True(stale.ExpireIfOverdue(stale.PaymentDeadline.AddSeconds(1)));
        await Assert.ThrowsAsync<OrderConcurrencyException>(() => store.SaveAsync(stale, [], None));

        // The expiry was queued once; the refused stale save queued nothing.
        var statuses = await outbox.ListForOrderAsync(created.OrderId, None);
        Assert.Equal([1, 2], statuses.Select(status => status.Sequence));
        Assert.All(statuses, status => Assert.Equal((0, (DateTimeOffset?)null), (status.Attempts, status.DeliveredAt)));
    }

    [Fact]
    public async Task OutboxClaims_SkipMessagesBeingClaimedAndKeepEachOrderInSequence()
    {
        var time = new ManualTimeProvider(Start);
        await using var instanceA = fixture.BuildServices(time);
        await using var instanceB = fixture.BuildServices(time);
        var first = await CreateOrderAsync(instanceA); // two notifications: created, then expired
        var store = instanceA.GetRequiredService<IOrderStore>();
        var loaded = (await store.FindAsync(first.OrderId, None))!;
        Assert.True(loaded.ExpireIfOverdue(loaded.PaymentDeadline.AddSeconds(1)));
        await store.SaveAsync(loaded, [], None);
        var second = await CreateOrderAsync(instanceA); // one notification
        var mine = new HashSet<Guid> { first.OrderId, second.OrderId };
        var outboxA = instanceA.GetRequiredService<IOutbox>();
        var outboxB = instanceB.GetRequiredService<IOutbox>();
        var now = Start.AddYears(10); // everything in the shared database is due by then
        var lease = TimeSpan.FromMinutes(5);
        var firstMessageId = (await outboxA.ListForOrderAsync(first.OrderId, None))[0].MessageId;

        // Another instance is in the middle of claiming the first order's opening message, so its row is locked.
        IReadOnlyList<OutboxMessage> claimedByB;
        await using (var connection = await instanceA.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using (var lockRow = new NpgsqlCommand("SELECT 1 FROM ops.outbox_message WHERE message_id = @id FOR UPDATE", connection, transaction))
            {
                lockRow.Parameters.AddWithValue("id", firstMessageId);
                await lockRow.ExecuteScalarAsync();
            }

            claimedByB = await outboxB.ClaimDueAsync(now, lease, 10_000, None).WaitAsync(TimeSpan.FromSeconds(10));
            await transaction.RollbackAsync();
        }

        // B neither waited for that message nor took it, nor jumped ahead to the first order's next one.
        Assert.Equal([(second.OrderId, 1)], claimedByB.Where(m => mine.Contains(m.OrderId)).Select(m => (m.OrderId, m.Sequence)));

        var claimedByA = Assert.Single(await outboxA.ClaimDueAsync(now, lease, 10_000, None), m => mine.Contains(m.OrderId));
        Assert.Equal((firstMessageId, 1), (claimedByA.MessageId, claimedByA.Sequence));
        using (var payload = JsonDocument.Parse(claimedByA.Payload))
        {
            Assert.Equal(firstMessageId, payload.RootElement.GetProperty("id").GetGuid());
            Assert.Equal("AwaitingFiatPayment", payload.RootElement.GetProperty("data").GetProperty("status").GetString());
        }

        // Both messages are leased now: nothing more is handed out for these orders, even the next one in line.
        Assert.DoesNotContain(await outboxB.ClaimDueAsync(now, lease, 10_000, None), m => mine.Contains(m.OrderId));

        var secondMessage = claimedByB.Single(m => m.OrderId == second.OrderId);
        await outboxA.MarkDeliveredAsync(firstMessageId, now, None);
        await outboxB.ScheduleRetryAsync(secondMessage.MessageId, now.AddMinutes(1), "HTTP_500", None);
        var next = (await outboxB.ClaimDueAsync(now, lease, 10_000, None)).Where(m => mine.Contains(m.OrderId));
        Assert.Equal([(first.OrderId, 2)], next.Select(m => (m.OrderId, m.Sequence)));

        var retry = Assert.Single(await outboxA.ClaimDueAsync(now.AddMinutes(1), lease, 10_000, None), m => mine.Contains(m.OrderId));
        Assert.Equal((secondMessage.MessageId, 1), (retry.MessageId, retry.Attempts));

        await outboxA.MarkDeadAsync(retry.MessageId, "HTTP_500", now.AddMinutes(1), None);
        var gaveUp = Assert.Single(await outboxB.ListForOrderAsync(second.OrderId, None));
        Assert.Equal((2, "HTTP_500", now.AddMinutes(1)), (gaveUp.Attempts, gaveUp.LastError, gaveUp.DeadAt));
        var delivered = (await outboxB.ListForOrderAsync(first.OrderId, None))[0];
        Assert.Equal((1, now), (delivered.Attempts, delivered.DeliveredAt));
    }

    [Fact]
    public async Task ReconciliationRuns_AreKeptUnchangedAndListedNewestFirst()
    {
        await using var services = fixture.BuildServices(TimeProvider.System);
        var store = services.GetRequiredService<IReconciliationStore>();
        var catalog = services.GetRequiredService<AssetCatalog>();
        var inr = catalog.Get(AssetId.Fiat("INR"));
        var day = new DateOnly(2031, 5, 1); // no other test reconciles this day
        var (start, end) = BusinessDay.Window(day, TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"));
        var orderId = Guid.NewGuid();
        Discrepancy[] found =
        [
            new(DiscrepancyType.MissingInLedger, "FIAT_RECEIVED", "pay_a", orderId, new Money(10_000m, inr), null),
            new(DiscrepancyType.MissingInReport, "FIAT_REFUNDED", "rfd_b", null, null, new Money(500.5m, inr)),
            new(DiscrepancyType.AmountMismatch, "FIAT_RECEIVED", "pay_c", null, new Money(2_000m, inr), new Money(1_999.99m, inr)),
            new(DiscrepancyType.DuplicateInLedger, "FIAT_RECEIVED", "pay_d", null, new Money(1m, inr), new Money(1m, inr)),
            new(DiscrepancyType.DuplicateInReport, "FIAT_RECEIVED", "pay_e", null, new Money(3m, inr), null),
        ];
        var first = new ReconciliationRun(Guid.NewGuid(), "sandbox-fiat", day, start, end, end.AddHours(12), 6, 5, 1, found);
        var second = first with { RunId = Guid.NewGuid(), RanAt = end.AddHours(36), Matched = 6, Discrepancies = [] };

        await store.SaveAsync(first, None);
        await store.SaveAsync(second, None);
        var runs = await store.ListAsync(day, 10, None);

        Assert.Equal([second.RunId, first.RunId], runs.Select(run => run.RunId));
        Assert.True(runs[0].Clean);
        var reloaded = runs[1];
        Assert.Equal((day, start, end, 6, 5, 1), (reloaded.Day, reloaded.WindowStart, reloaded.WindowEnd, reloaded.ReportLines, reloaded.LedgerEntries, reloaded.Matched));
        Assert.Equal(found, reloaded.Discrepancies);

        await using var connection = await services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync();
        await using var edit = new NpgsqlCommand("UPDATE ops.reconciliation_discrepancy SET reference = 'edited' WHERE run_id = @id", connection);
        edit.Parameters.AddWithValue("id", first.RunId);
        Assert.Equal("LG003", (await Assert.ThrowsAsync<PostgresException>(() => edit.ExecuteNonQueryAsync())).SqlState);
    }

    [Fact]
    public async Task SandboxDay_IsReconciledAgainstTheDatabaseLedger()
    {
        // A day of its own (11:30 IST, 15 March 2027), so no other test's entries fall in it.
        var time = new ManualTimeProvider(new DateTimeOffset(2027, 3, 15, 6, 0, 0, TimeSpan.Zero));
        await using var services = fixture.BuildServices(time);
        var webhooks = services.GetRequiredService<SandboxFiatWebhookHandler>();
        var paid = await CreateOrderAsync(services);
        var (body, signature) = webhooks.CreateSignedPaymentEvent(paid);
        Assert.Equal(WebhookResult.Accepted, await webhooks.HandleAsync(body, signature, None));
        var lost = await CreateOrderAsync(services);
        webhooks.CreateSignedPaymentEvent(lost); // captured by the provider, never delivered

        time.Advance(TimeSpan.FromDays(1));
        var run = Assert.Single(await services.GetRequiredService<ReconciliationService>().RunAsync(new DateOnly(2027, 3, 15), None));

        Assert.Equal((2, 1, 1), (run.ReportLines, run.LedgerEntries, run.Matched));
        var discrepancy = Assert.Single(run.Discrepancies);
        Assert.Equal((DiscrepancyType.MissingInLedger, (Guid?)lost.OrderId), (discrepancy.Type, discrepancy.OrderId));
        Assert.Equal(run.RunId, Assert.Single(await services.GetRequiredService<IReconciliationStore>().ListAsync(run.Day, 10, None)).RunId);

        // An entry is found by its time in the day, or by its provider ID from any day, and only for its provider.
        var ledger = services.GetRequiredService<ILedgerStore>();
        var paymentId = (await services.GetRequiredService<IOrderStore>().FindAsync(paid.OrderId, None))!.FiatPayment!.ProviderPaymentId;
        var farAway = new DateTimeOffset(2040, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal([paymentId], (await ledger.ListForReconciliationAsync("sandbox-fiat", run.WindowStart, run.WindowEnd, [], None)).Select(e => e.External!.Id));
        Assert.Equal([paymentId], (await ledger.ListForReconciliationAsync("sandbox-fiat", farAway, farAway.AddDays(1), [paymentId], None)).Select(e => e.External!.Id));
        Assert.Empty(await ledger.ListForReconciliationAsync("sandbox-fiat", farAway, farAway.AddDays(1), [], None));
        Assert.Empty(await ledger.ListForReconciliationAsync("another-provider", run.WindowStart, run.WindowEnd, [paymentId], None));
    }

    [Fact]
    public async Task SettlementWallets_AreStoredWithTheirHistory()
    {
        var time = new ManualTimeProvider(Start);
        await using var first = fixture.BuildServices(time);
        var merchant = await AddMerchantAsync(first);
        var settlement = first.GetRequiredService<SettlementService>();
        var (clean, _) = await settlement.RegisterAsync(merchant, "polygon", Address, "Main", None);
        var (risky, _) = await settlement.RegisterAsync(merchant, "ethereum", SandboxWalletScreening.HighRiskAddress, null, None);
        await settlement.ApproveAsync(risky.WalletId, "officer", "Verified with the merchant.", None);
        time.Advance(TimeSpan.FromHours(30));
        var (replacement, _) = await settlement.RegisterAsync(merchant, "polygon", "0x3333333333333333333333333333333333333333", null, None);

        // Another instance shares nothing with the first except the database.
        await using var second = fixture.BuildServices(time);
        var reloaded = await second.GetRequiredService<ISettlementStore>().GetAsync(merchant, None);

        Assert.Equal(4, reloaded.Version); // two registrations, an approval, a replacement
        Assert.Equal([clean.WalletId, risky.WalletId, replacement.WalletId], reloaded.Wallets.Select(wallet => wallet.WalletId));
        var storedClean = reloaded.Find(clean.WalletId)!;
        Assert.Equal(("polygon", Address, "Main", WalletRisk.Low), (storedClean.Network, storedClean.Address, storedClean.Label, storedClean.Screening.Risk));
        Assert.Equal(replacement.UsableFrom, storedClean.RetiredAt); // scheduled to hand over
        Assert.Equal(
            [(SettlementWalletActionKind.Registered, merchant, (string?)null), (SettlementWalletActionKind.Approved, "officer", "Verified with the merchant.")],
            reloaded.Find(risky.WalletId)!.History.Select(action => (action.Kind, action.Actor, action.Reason)));
        Assert.Equal(risky.WalletId, (await second.GetRequiredService<SettlementService>().CurrentAsync(merchant, "ethereum", None))!.WalletId);
        Assert.Equal(merchant, await second.GetRequiredService<ISettlementStore>().FindOwnerAsync(risky.WalletId, None));
    }

    [Fact]
    public async Task StaleSettlementCopy_CannotOverwriteANewerChange()
    {
        await using var services = fixture.BuildServices(new ManualTimeProvider(Start));
        var merchant = await AddMerchantAsync(services);
        var store = services.GetRequiredService<ISettlementStore>();
        var clean = new WalletScreeningResult(WalletRisk.Low, "test", null);
        var first = await store.GetAsync(merchant, None);
        var second = await store.GetAsync(merchant, None);
        first.Register(Guid.NewGuid(), "polygon", Address, null, clean, merchant, Start, TimeSpan.Zero, out _);
        await store.SaveAsync(first, None);

        second.Register(Guid.NewGuid(), "tron", "TLyqzVGLV1srkB7dToTAEqgDSfPtXRJZYH", null, clean, merchant, Start, TimeSpan.Zero, out _);

        await Assert.ThrowsAsync<SettlementConcurrencyException>(() => store.SaveAsync(second, None));
        Assert.Single((await store.GetAsync(merchant, None)).Wallets);
    }

    [Fact]
    public async Task UnderReviewWallets_AreListedLongestWaitingFirst()
    {
        var time = new ManualTimeProvider(Start);
        await using var services = fixture.BuildServices(time);
        var merchant = await AddMerchantAsync(services);
        var settlement = services.GetRequiredService<SettlementService>();
        var (older, _) = await settlement.RegisterAsync(merchant, "ethereum", SandboxWalletScreening.HighRiskAddress, null, None);
        time.Advance(TimeSpan.FromMinutes(5));
        var (newer, _) = await settlement.RegisterAsync(merchant, "polygon", SandboxWalletScreening.OutageAddress, null, None);

        var queue = (await settlement.ListForReviewAsync(None)).Where(wallet => wallet.ClientId == merchant).ToList();

        Assert.Equal([older.WalletId, newer.WalletId], queue.Select(wallet => wallet.WalletId));
        Assert.Equal(("screening-unavailable", WalletRisk.Unknown), (queue[1].Screening.Screener, queue[1].Screening.Risk));
    }

    /// <summary>A merchant no other test uses, so settlement state never leaks between tests.</summary>
    private static async Task<string> AddMerchantAsync(ServiceProvider services)
    {
        var clientId = $"merchant-{Guid.NewGuid():N}";
        await using var connection = await services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "INSERT INTO gateway.api_client (client_id, display_name, key_sha256, role) VALUES (@id, @id, @hash, 'MERCHANT')", connection);
        command.Parameters.AddWithValue("id", clientId);
        command.Parameters.AddWithValue("hash", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(clientId))));
        await command.ExecuteNonQueryAsync();
        return clientId;
    }

    /// <summary>An order for a merchant of its own, paid out to <paramref name="address"/>.</summary>
    private static async Task<PaymentOrder> CreateOrderAsync(ServiceProvider services, string address = Address)
    {
        var merchant = await MerchantWithWalletAsync(services, address);
        var onRamp = services.GetRequiredService<OnRampService>();
        var quote = await onRamp.CreateQuoteAsync(merchant, new CreateQuoteCommand("INR", 10_000m, "USDC@ethereum"), None);
        return await onRamp.CreateOrderAsync(merchant, new CreateOrderCommand(quote.QuoteId, SandboxCustomerDirectory.Verified), None);
    }

    /// <summary>
    /// A merchant no other test uses, with <paramref name="address"/> as its active Ethereum wallet (approved if
    /// screening held it). Tests run on different clocks, so they must not share a merchant's settlement wallets.
    /// </summary>
    private static async Task<string> MerchantWithWalletAsync(ServiceProvider services, string address = Address)
    {
        var merchant = await AddMerchantAsync(services);
        var settlement = services.GetRequiredService<SettlementService>();
        var (wallet, _) = await settlement.RegisterAsync(merchant, "ethereum", address, null, None);
        if (wallet.Status == SettlementWalletStatus.UnderReview)
        {
            await settlement.ApproveAsync(wallet.WalletId, "officer", "Test wallet, approved for this scenario.", None);
        }

        return merchant;
    }

    /// <summary>Every file in db/migrations, by version, so a new migration needs no test change.</summary>
    private static IEnumerable<string> MigrationFileVersions()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FiatCryptoGateway.sln")))
        {
            directory = directory.Parent;
        }

        var migrations = Path.Combine(directory?.FullName ?? throw new InvalidOperationException("Repository root not found."), "db", "migrations");
        return Directory.GetFiles(migrations, "*.sql").Select(Path.GetFileNameWithoutExtension).OfType<string>().Order(StringComparer.Ordinal);
    }

    /// <summary>Stores an order directly, for a customer ID no other test uses.</summary>
    private static async Task<PaymentOrder> AddOrderAsync(ServiceProvider services, string customerId, TimeProvider time)
    {
        var quote = await services.GetRequiredService<OnRampService>()
            .CreateQuoteAsync(Client, new CreateQuoteCommand("INR", 10_000m, "USDC@ethereum"), None);
        var order = PaymentOrder.Create(Guid.NewGuid(), Client, customerId, quote, Address, time.GetUtcNow(), TimeSpan.FromMinutes(15));
        await services.GetRequiredService<IOrderStore>().AddAsync(order, None);
        return order;
    }

    private static string DbName(OrderStatus status) => PascalBoundary().Replace(status.ToString(), "_$1").ToUpperInvariant();

    [GeneratedRegex("(?<!^)([A-Z])")]
    private static partial Regex PascalBoundary();

    /// <summary>Records captured fiat without running the next steps, so two instances can race to run them.</summary>
    private static async Task<PaymentOrder> CreatePaidButUnprocessedOrderAsync(ServiceProvider services, TimeProvider time)
    {
        var created = await CreateOrderAsync(services);
        var store = services.GetRequiredService<IOrderStore>();
        var order = (await store.FindAsync(created.OrderId, None))!;
        var payment = new FiatPaymentConfirmation(
            SandboxFiatProvider.ProviderName, $"pay_race_{order.OrderId:N}", order.OrderId, order.Quote.SourceAmount, time.GetUtcNow());
        order.RecordFiatPayment(payment, time.GetUtcNow());
        await store.SaveAsync(order, [OnRampPostings.FiatReceived(order, payment, time.GetUtcNow())], None);
        return order;
    }
}
