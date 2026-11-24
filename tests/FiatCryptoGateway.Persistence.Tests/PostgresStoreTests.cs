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
public sealed class PostgresStoreTests(PostgresFixture fixture)
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
        Assert.Equal("001_gateway_foundation,002_persistence", await command.ExecuteScalarAsync());
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

    private static async Task<PaymentOrder> CreateOrderAsync(ServiceProvider services)
    {
        var onRamp = services.GetRequiredService<OnRampService>();
        var quote = await onRamp.CreateQuoteAsync(Client, new CreateQuoteCommand("INR", 10_000m, "USDC@ethereum"), None);
        return await onRamp.CreateOrderAsync(Client, new CreateOrderCommand(quote.QuoteId, SandboxCustomerDirectory.Verified, Address), None);
    }

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
