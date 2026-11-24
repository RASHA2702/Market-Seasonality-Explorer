using FiatCryptoGateway.Application;
using FiatCryptoGateway.Application.OnRamp;
using FiatCryptoGateway.Application.Ports;
using FiatCryptoGateway.Domain;
using FiatCryptoGateway.Domain.Assets;
using FiatCryptoGateway.Domain.Ledger;
using FiatCryptoGateway.Domain.Orders;
using FiatCryptoGateway.Sandbox;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FiatCryptoGateway.Tests;

/// <summary>The whole on-ramp flow through the real services and the sandbox providers.</summary>
public sealed class OnRampServiceTests : IDisposable
{
    private const string Client = "client-a";
    private const string Address = "0x1111111111111111111111111111111111111111";
    private static readonly CancellationToken None = CancellationToken.None;

    private readonly ManualTimeProvider _time = new(Fixtures.Start);
    private readonly ServiceProvider _services;

    public OnRampServiceTests()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(_time);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddGatewayCore(configuration);
        services.AddSandboxProviders(configuration);
        services.AddInMemoryStores();
        _services = services.BuildServiceProvider(validateScopes: true);
    }

    private OnRampService OnRamp => _services.GetRequiredService<OnRampService>();

    private SandboxFiatWebhookHandler Webhooks => _services.GetRequiredService<SandboxFiatWebhookHandler>();

    private ILedgerStore Ledger => _services.GetRequiredService<ILedgerStore>();

    public void Dispose() => _services.Dispose();

    [Fact]
    public async Task FullFlow_DeliversCryptoAndBalancesTheLedger()
    {
        var order = await CreateOrderAsync(10_000m);
        Assert.Equal(OrderStatus.AwaitingFiatPayment, order.Status);
        Assert.NotNull(order.CheckoutUrl);

        Assert.Equal(WebhookResult.Accepted, await PayAsync(order));
        Assert.Equal(OrderStatus.CryptoSent, order.Status);

        await OnRamp.RecordConfirmationsAsync(Client, order.OrderId, order.Transfer!.TransactionHash, 12, None);
        Assert.Equal(OrderStatus.Completed, order.Status);

        var entries = await Ledger.ListAsync(order.OrderId, None);
        Assert.Equal(["FIAT_RECEIVED", "CRYPTO_PURCHASED", "CRYPTO_SENT"], entries.Select(entry => entry.Kind));
        AssertBalancedAndCustomerCleared(LedgerBalances.Compute(entries));
    }

    [Fact]
    public async Task RedeliveredWebhook_IsProcessedOnce()
    {
        var order = await CreateOrderAsync(10_000m);
        var (body, signature) = Webhooks.CreateSignedPaymentEvent(order);

        Assert.Equal(WebhookResult.Accepted, await Webhooks.HandleAsync(body, signature, None));
        Assert.Equal(WebhookResult.Duplicate, await Webhooks.HandleAsync(body, signature, None));

        var entries = await Ledger.ListAsync(order.OrderId, None);
        Assert.Single(entries, entry => entry.Kind == "FIAT_RECEIVED");
    }

    [Fact]
    public async Task TamperedWebhook_IsIgnored()
    {
        var order = await CreateOrderAsync(10_000m);
        var (body, signature) = Webhooks.CreateSignedPaymentEvent(order);

        var result = await Webhooks.HandleAsync(body.Replace("10000.00", "99999.00", StringComparison.Ordinal), signature, None);

        Assert.Equal(WebhookResult.InvalidSignature, result);
        Assert.Equal(OrderStatus.AwaitingFiatPayment, order.Status);
    }

    [Theory]
    [InlineData(SandboxCustomerDirectory.Unverified, Address, "KYC_REQUIRED")]
    [InlineData(SandboxCustomerDirectory.BlockedCountry, Address, "COUNTRY_NOT_SUPPORTED")]
    [InlineData(SandboxCustomerDirectory.SanctionsMatch, Address, "ORDER_NOT_PERMITTED")]
    [InlineData(SandboxCustomerDirectory.Verified, SandboxWalletScreening.BlockedAddress, "ORDER_NOT_PERMITTED")]
    public async Task IneligibleOrders_AreRefusedBeforeAnyPayment(string customer, string address, string code)
    {
        var quote = await QuoteAsync(10_000m);

        var exception = await Assert.ThrowsAsync<GatewayRuleException>(() =>
            OnRamp.CreateOrderAsync(Client, new CreateOrderCommand(quote.QuoteId, customer, address), None));

        // Sanctions and wallet findings surface only as ORDER_NOT_PERMITTED, so a suspect is not tipped off.
        Assert.Equal(code, exception.Code);
    }

    [Fact]
    public async Task ScreeningOutage_HoldsTheOrderForAPerson()
    {
        var order = await CreateOrderAsync(10_000m, address: SandboxWalletScreening.OutageAddress);

        await PayAsync(order);
        Assert.Equal(OrderStatus.ComplianceReview, order.Status);
        Assert.Equal(["SCREENING_UNAVAILABLE"], order.Compliance!.Reasons);

        await OnRamp.RecordReviewDecisionAsync(Client, order.OrderId, approve: true, "analyst-1", None);
        Assert.Equal(OrderStatus.CryptoSent, order.Status);
    }

    [Fact]
    public async Task LargeOrder_WaitsForReviewAndCanBeRejected()
    {
        var order = await CreateOrderAsync(6_000m, currency: "USD");

        await PayAsync(order);
        Assert.Equal(OrderStatus.ComplianceReview, order.Status);

        await OnRamp.RecordReviewDecisionAsync(Client, order.OrderId, approve: false, "analyst-1", None);
        Assert.Equal(OrderStatus.ComplianceRejected, order.Status);
        Assert.Equal(0, _services.GetRequiredService<SandboxLiquidityProvider>().ExecutionCount);
    }

    [Fact]
    public async Task LatePayment_IsRefundedNotConverted()
    {
        var order = await CreateOrderAsync(10_000m);
        _time.Advance(TimeSpan.FromMinutes(16));

        await PayAsync(order);

        Assert.Equal(OrderStatus.Refunded, order.Status);
        Assert.Equal(0, _services.GetRequiredService<SandboxLiquidityProvider>().ExecutionCount);
        var balances = LedgerBalances.Compute(await Ledger.ListAsync(order.OrderId, None));
        Assert.All(balances, balance => Assert.Equal(0m, balance.DebitMinusCredit));
    }

    [Fact]
    public async Task UnpaidOrder_ExpiresAfterItsPaymentWindow()
    {
        var order = await CreateOrderAsync(10_000m);

        Assert.Equal(0, await OnRamp.ExpireOverdueOrdersAsync(None));
        _time.Advance(TimeSpan.FromMinutes(16));
        Assert.Equal(1, await OnRamp.ExpireOverdueOrdersAsync(None));

        Assert.Equal(OrderStatus.Expired, order.Status);
    }

    [Fact]
    public async Task Quote_CanBeUsedForOneOrderOnly()
    {
        var quote = await QuoteAsync(10_000m);
        await OnRamp.CreateOrderAsync(Client, new CreateOrderCommand(quote.QuoteId, SandboxCustomerDirectory.Verified, Address), None);

        var exception = await Assert.ThrowsAsync<GatewayRuleException>(() =>
            OnRamp.CreateOrderAsync(Client, new CreateOrderCommand(quote.QuoteId, SandboxCustomerDirectory.Verified, Address), None));

        Assert.Equal("QUOTE_ALREADY_USED", exception.Code);
    }

    [Fact]
    public async Task ExpiredQuote_IsRefused()
    {
        var quote = await QuoteAsync(10_000m);
        _time.Advance(TimeSpan.FromSeconds(61));

        var exception = await Assert.ThrowsAsync<GatewayRuleException>(() =>
            OnRamp.CreateOrderAsync(Client, new CreateOrderCommand(quote.QuoteId, SandboxCustomerDirectory.Verified, Address), None));

        Assert.Equal("QUOTE_EXPIRED", exception.Code);
    }

    [Fact]
    public async Task AnotherClient_CannotUseOrSeeAQuoteOrOrder()
    {
        var order = await CreateOrderAsync(10_000m);
        var quote = await QuoteAsync(10_000m);

        await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            OnRamp.CreateOrderAsync("client-b", new CreateOrderCommand(quote.QuoteId, SandboxCustomerDirectory.Verified, Address), None));
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => OnRamp.GetOrderAsync("client-b", order.OrderId, None));
    }

    [Theory]
    [InlineData("0x123")]
    [InlineData("bc1qar0srrr7xfkvy5l643lydnw9re59gtzzwf5mdq")]
    public async Task AddressThatIsNotValidForTheNetwork_IsRefused(string address)
    {
        var quote = await QuoteAsync(10_000m);

        var exception = await Assert.ThrowsAsync<GatewayRuleException>(() =>
            OnRamp.CreateOrderAsync(Client, new CreateOrderCommand(quote.QuoteId, SandboxCustomerDirectory.Verified, address), None));

        Assert.Equal("INVALID_DESTINATION_ADDRESS", exception.Code);
    }

    [Fact]
    public async Task ReprocessingACompletedOrder_NeverBuysOrSendsTwice()
    {
        var order = await CreateOrderAsync(10_000m);
        await PayAsync(order);

        await OnRamp.ProcessAsync(order.OrderId, None);
        await OnRamp.ProcessPendingOrdersAsync(None);
        await OnRamp.ProcessAsync(order.OrderId, None);

        Assert.Equal(1, _services.GetRequiredService<SandboxLiquidityProvider>().ExecutionCount);
        Assert.Equal(1, _services.GetRequiredService<SandboxCustodyProvider>().TransferCount);
        Assert.Equal(3, (await Ledger.ListAsync(order.OrderId, None)).Count);
    }

    [Theory]
    [InlineData("XOF", "NO_PRICE_ROUTE")]      // a real ISO currency with no sandbox FX price
    [InlineData("ABC", "UNSUPPORTED_ASSET")]   // not a currency at all
    public async Task CurrenciesWithoutAPriceRoute_AreRefusedClearly(string currency, string code)
    {
        var exception = await Assert.ThrowsAsync<GatewayRuleException>(() =>
            OnRamp.CreateQuoteAsync(Client, new CreateQuoteCommand(currency, 50_000m, "USDC@ethereum"), None));

        Assert.Equal(code, exception.Code);
    }

    [Theory]
    [InlineData("EUR", "100.00", "BTC@bitcoin", "bc1qar0srrr7xfkvy5l643lydnw9re59gtzzwf5mdq")]
    [InlineData("JPY", "50000", "SOL@solana", "7EcDhSYGxXyscszYEp35KHN8vvw3svAuLKTzXwCFLtV")]
    [InlineData("KWD", "100.000", "ETH@ethereum", Address)]
    public async Task OtherCurrenciesAndNetworks_CompleteEndToEnd(string currency, string amount, string asset, string address)
    {
        var order = await CreateOrderAsync(
            decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), currency, asset, address);

        await PayAsync(order);
        await OnRamp.RecordConfirmationsAsync(Client, order.OrderId, order.Transfer!.TransactionHash, 100, None);

        Assert.Equal(OrderStatus.Completed, order.Status);
        AssertBalancedAndCustomerCleared(LedgerBalances.Compute(await Ledger.ListAsync(order.OrderId, None)));
    }

    private static void AssertBalancedAndCustomerCleared(IReadOnlyList<AccountBalance> balances)
    {
        foreach (var asset in balances.GroupBy(balance => balance.Asset))
        {
            Assert.Equal(0m, asset.Sum(balance => balance.DebitMinusCredit));
        }

        Assert.All(
            balances.Where(balance => balance.Account == "liabilities:customer_orders"),
            balance => Assert.Equal(0m, balance.DebitMinusCredit));
    }

    private Task<Domain.Pricing.Quote> QuoteAsync(decimal amount, string currency = "INR", string asset = "USDC@ethereum") =>
        OnRamp.CreateQuoteAsync(Client, new CreateQuoteCommand(currency, amount, asset), None);

    private async Task<PaymentOrder> CreateOrderAsync(
        decimal amount,
        string currency = "INR",
        string asset = "USDC@ethereum",
        string address = Address)
    {
        var quote = await QuoteAsync(amount, currency, asset);
        return await OnRamp.CreateOrderAsync(Client, new CreateOrderCommand(quote.QuoteId, SandboxCustomerDirectory.Verified, address), None);
    }

    private Task<WebhookResult> PayAsync(PaymentOrder order, Money? amount = null)
    {
        var (body, signature) = Webhooks.CreateSignedPaymentEvent(order, amount, _time.GetUtcNow());
        return Webhooks.HandleAsync(body, signature, None);
    }
}
