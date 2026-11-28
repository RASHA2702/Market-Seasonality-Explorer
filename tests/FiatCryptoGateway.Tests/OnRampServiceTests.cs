using FiatCryptoGateway.Application;
using FiatCryptoGateway.Application.Compliance;
using FiatCryptoGateway.Application.OnRamp;
using FiatCryptoGateway.Application.Ports;
using FiatCryptoGateway.Application.Settlement;
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
        // New settlement wallets are usable at once here; the cooling-off period has its own tests.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:Settlement:CoolingOffHours"] = "0",
                ["Gateway:Fiat:Provider"] = "SandboxPartner", // these tests pay by the simulated partner's webhook
            })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(_time);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddGatewayCore(configuration);
        services.AddSandboxProviders(configuration);
        services.AddInMemoryStores();
        _services = services.BuildServiceProvider(validateScopes: true);
    }

    private OnRampService OnRamp => _services.GetRequiredService<OnRampService>();

    private ComplianceReviewService Reviews => _services.GetRequiredService<ComplianceReviewService>();

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
    [InlineData(SandboxCustomerDirectory.Unverified, "KYC_REQUIRED")]
    [InlineData(SandboxCustomerDirectory.BlockedCountry, "COUNTRY_NOT_SUPPORTED")]
    [InlineData(SandboxCustomerDirectory.Foreign, "COUNTRY_NOT_SUPPORTED")]
    [InlineData(SandboxCustomerDirectory.SanctionsMatch, "ORDER_NOT_PERMITTED")]
    public async Task IneligibleOrders_AreRefusedBeforeAnyPayment(string customer, string code)
    {
        await UseWalletAsync(Address);
        var quote = await QuoteAsync(10_000m);

        var exception = await Assert.ThrowsAsync<GatewayRuleException>(() =>
            OnRamp.CreateOrderAsync(Client, new CreateOrderCommand(quote.QuoteId, customer), None));

        // Sanctions findings surface only as ORDER_NOT_PERMITTED, so a suspect is not tipped off.
        Assert.Equal(code, exception.Code);
    }

    [Fact]
    public async Task ScreeningOutage_HoldsTheOrderForAPerson()
    {
        var order = await CreateOrderAsync(10_000m, address: SandboxWalletScreening.OutageAddress);

        await PayAsync(order);
        Assert.Equal(OrderStatus.ComplianceReview, order.Status);
        Assert.Equal(["SCREENING_UNAVAILABLE"], order.Compliance!.Reasons);

        await Reviews.ReviewAsync(order.OrderId, ReviewActionKind.Approve, "analyst-1", "Wallet checked by hand.", null, None);
        Assert.Equal(OrderStatus.CryptoSent, order.Status);
        Assert.Equal("analyst-1", order.ReviewActions.Single().Reviewer);
    }

    [Fact]
    public async Task RejectedMoney_IsFrozenAndReportedThenRefundedOnlyAfterRelease()
    {
        var order = await CreateOrderAsync(6_000m, currency: "USD");
        await PayAsync(order);
        Assert.Equal(OrderStatus.ComplianceReview, order.Status);

        await Reviews.ReviewAsync(order.OrderId, ReviewActionKind.Reject, "analyst-1", "Source of funds not explained.", null, None);
        Assert.Equal(OrderStatus.ComplianceRejected, order.Status);

        await Reviews.ReviewAsync(order.OrderId, ReviewActionKind.FreezeAndReport, "analyst-1", "Reported as suspicious.", "STR-2026-0001", None);
        Assert.Equal(OrderStatus.Frozen, order.Status);
        Assert.Equal("STR-2026-0001", order.ReportReference);

        await Reviews.ReviewAsync(order.OrderId, ReviewActionKind.ReleaseForRefund, "analyst-1", "Release instructed in writing.", null, None);
        Assert.Equal(OrderStatus.Refunded, order.Status);

        Assert.Equal(0, _services.GetRequiredService<SandboxLiquidityProvider>().ExecutionCount);
        Assert.Equal(
            [ReviewActionKind.Reject, ReviewActionKind.FreezeAndReport, ReviewActionKind.ReleaseForRefund],
            order.ReviewActions.Select(action => action.Kind));
        var balances = LedgerBalances.Compute(await Ledger.ListAsync(order.OrderId, None));
        Assert.All(balances, balance => Assert.Equal(0m, balance.DebitMinusCredit));
    }

    [Fact]
    public async Task DailyTotal_RefusesTheOrderThatWouldExceedItUntilTheWindowPasses()
    {
        // Basic KYC: 1,000 per payment, 2,000 per rolling day, 5,000 per rolling month.
        await CreateOrderAsync(900m, currency: "USD", customer: SandboxCustomerDirectory.BasicKyc);
        await CreateOrderAsync(900m, currency: "USD", customer: SandboxCustomerDirectory.BasicKyc);

        var exception = await Assert.ThrowsAsync<GatewayRuleException>(() =>
            CreateOrderAsync(900m, currency: "USD", customer: SandboxCustomerDirectory.BasicKyc));
        Assert.Equal("DAILY_LIMIT_EXCEEDED", exception.Code);

        _time.Advance(TimeSpan.FromHours(25));
        var order = await CreateOrderAsync(900m, currency: "USD", customer: SandboxCustomerDirectory.BasicKyc);
        Assert.Equal(OrderStatus.AwaitingFiatPayment, order.Status);
    }

    [Fact]
    public async Task ExpiredOrders_DoNotCountTowardsLimits()
    {
        await CreateOrderAsync(900m, currency: "USD", customer: SandboxCustomerDirectory.BasicKyc);
        _time.Advance(TimeSpan.FromMinutes(16));
        Assert.Equal(1, await OnRamp.ExpireOverdueOrdersAsync(None));

        await CreateOrderAsync(900m, currency: "USD", customer: SandboxCustomerDirectory.BasicKyc);
        var second = await CreateOrderAsync(900m, currency: "USD", customer: SandboxCustomerDirectory.BasicKyc);

        Assert.Equal(OrderStatus.AwaitingFiatPayment, second.Status);
    }

    [Fact]
    public async Task ReviewQueue_ListsHeldOrdersOldestFirstAndFlagsOverdueOnes()
    {
        var older = await CreateOrderAsync(6_000m, currency: "USD");
        await PayAsync(older);
        _time.Advance(TimeSpan.FromHours(20));
        var newer = await CreateOrderAsync(10_000m, address: SandboxWalletScreening.HighRiskAddress);
        await PayAsync(newer);

        var queue = await Reviews.GetQueueAsync(None);
        Assert.Equal([older.OrderId, newer.OrderId], queue.Select(item => item.Order.OrderId));
        Assert.All(queue, item => Assert.False(item.Overdue));

        _time.Advance(TimeSpan.FromHours(5));
        queue = await Reviews.GetQueueAsync(None);
        Assert.True(queue[0].Overdue);   // waiting 25 hours, over the 24-hour limit
        Assert.False(queue[1].Overdue);  // waiting 5 hours
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
        await UseWalletAsync(Address);
        var quote = await QuoteAsync(10_000m);
        await OnRamp.CreateOrderAsync(Client, new CreateOrderCommand(quote.QuoteId, SandboxCustomerDirectory.Verified), None);

        var exception = await Assert.ThrowsAsync<GatewayRuleException>(() =>
            OnRamp.CreateOrderAsync(Client, new CreateOrderCommand(quote.QuoteId, SandboxCustomerDirectory.Verified), None));

        Assert.Equal("QUOTE_ALREADY_USED", exception.Code);
    }

    [Fact]
    public async Task ExpiredQuote_IsRefused()
    {
        await UseWalletAsync(Address);
        var quote = await QuoteAsync(10_000m);
        _time.Advance(TimeSpan.FromSeconds(61));

        var exception = await Assert.ThrowsAsync<GatewayRuleException>(() =>
            OnRamp.CreateOrderAsync(Client, new CreateOrderCommand(quote.QuoteId, SandboxCustomerDirectory.Verified), None));

        Assert.Equal("QUOTE_EXPIRED", exception.Code);
    }

    [Fact]
    public async Task AnotherClient_CannotUseOrSeeAQuoteOrOrder()
    {
        var order = await CreateOrderAsync(10_000m);
        var quote = await QuoteAsync(10_000m);

        await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            OnRamp.CreateOrderAsync("client-b", new CreateOrderCommand(quote.QuoteId, SandboxCustomerDirectory.Verified), None));
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => OnRamp.GetOrderAsync("client-b", order.OrderId, None));
    }

    [Fact]
    public async Task WalletOnAnotherNetwork_DoesNotReceiveTheOrder()
    {
        await UseWalletAsync(Address, "polygon");
        var quote = await QuoteAsync(10_000m); // USDC on Ethereum

        var exception = await Assert.ThrowsAsync<GatewayRuleException>(() =>
            OnRamp.CreateOrderAsync(Client, new CreateOrderCommand(quote.QuoteId, SandboxCustomerDirectory.Verified), None));

        Assert.Equal("NO_SETTLEMENT_WALLET", exception.Code);
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
    [InlineData("EUR", "100.00", "USDT@tron", "TLyqzVGLV1srkB7dToTAEqgDSfPtXRJZYH")]
    [InlineData("GBP", "50.00", "USDC@polygon", Address)]
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
            balances.Where(balance => balance.Account is "liabilities:funds_held_for_customers" or "liabilities:payable_to_merchants"),
            balance => Assert.Equal(0m, balance.DebitMinusCredit));
    }

    private Task<Domain.Pricing.Quote> QuoteAsync(decimal amount, string currency = "INR", string asset = "USDC@ethereum") =>
        OnRamp.CreateQuoteAsync(Client, new CreateQuoteCommand(currency, amount, asset), None);

    /// <summary>Creates an order paying out to <paramref name="address"/>, made the merchant's active wallet first.</summary>
    private async Task<PaymentOrder> CreateOrderAsync(
        decimal amount,
        string currency = "INR",
        string asset = "USDC@ethereum",
        string address = Address,
        string customer = SandboxCustomerDirectory.Verified)
    {
        await UseWalletAsync(address, AssetId.Parse(asset).Network!);
        var quote = await QuoteAsync(amount, currency, asset);
        return await OnRamp.CreateOrderAsync(Client, new CreateOrderCommand(quote.QuoteId, customer), None);
    }

    /// <summary>Makes <paramref name="address"/> the merchant's active wallet, approving it if screening held it.</summary>
    private async Task UseWalletAsync(string address, string network = "ethereum")
    {
        var settlement = _services.GetRequiredService<SettlementService>();
        var (wallet, _) = await settlement.RegisterAsync(Client, network, address, null, None);
        if (wallet.Status == Domain.Settlement.SettlementWalletStatus.UnderReview)
        {
            await settlement.ApproveAsync(wallet.WalletId, "officer", "Test wallet, approved for this scenario.", None);
        }

        Assert.Equal(address, (await settlement.CurrentAsync(Client, network, None))!.Address);
    }

    private Task<WebhookResult> PayAsync(PaymentOrder order, Money? amount = null)
    {
        var (body, signature) = Webhooks.CreateSignedPaymentEvent(order, amount, _time.GetUtcNow());
        return Webhooks.HandleAsync(body, signature, None);
    }
}
