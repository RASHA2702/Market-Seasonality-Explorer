using System.Text.Json;
using System.Text.RegularExpressions;
using FiatCryptoGateway.Application.Compliance;
using FiatCryptoGateway.Application.OnRamp;
using FiatCryptoGateway.Application.Ports;
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
        var onRamp = first.GetRequiredService<OnRampService>();
        var quote = await onRamp.CreateQuoteAsync(Client, new CreateQuoteCommand("INR", 10_000m, "USDC@ethereum"), None);
        var order = await onRamp.CreateOrderAsync(Client, new CreateOrderCommand(quote.QuoteId, SandboxCustomerDirectory.Verified, Address), None);

        var webhooks = first.GetRequiredService<SandboxFiatWebhookHandler>();
        var (body, signature) = webhooks.CreateSignedPaymentEvent(order);
        Assert.Equal(WebhookResult.Accepted, await webhooks.HandleAsync(body, signature, None));
        var sent = await onRamp.GetOrderAsync(Client, order.OrderId, None);
        await onRamp.RecordConfirmationsAsync(Client, order.OrderId, sent.Transfer!.TransactionHash, 12, None);

        // A second instance shares nothing with the first except the database.
        await using var second = fixture.BuildServices(time);
        var reloaded = await second.GetRequiredService<OnRampService>().GetOrderAsync(Client, order.OrderId, None);

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

        // Basic KYC allows 2,000 USD per day: three 900 USD orders arrive at once on two instances.
        var results = await Task.WhenAll(new[] { instanceA, instanceB, instanceA }.Select(async services =>
        {
            var onRamp = services.GetRequiredService<OnRampService>();
            var quote = await onRamp.CreateQuoteAsync(Client, new CreateQuoteCommand("USD", 900m, "USDC@ethereum"), None);
            try
            {
                await onRamp.CreateOrderAsync(Client, new CreateOrderCommand(quote.QuoteId, SandboxCustomerDirectory.BasicKyc, Address), None);
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

    private static async Task<PaymentOrder> CreateOrderAsync(ServiceProvider services, string address = Address)
    {
        var onRamp = services.GetRequiredService<OnRampService>();
        var quote = await onRamp.CreateQuoteAsync(Client, new CreateQuoteCommand("INR", 10_000m, "USDC@ethereum"), None);
        return await onRamp.CreateOrderAsync(Client, new CreateOrderCommand(quote.QuoteId, SandboxCustomerDirectory.Verified, address), None);
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
