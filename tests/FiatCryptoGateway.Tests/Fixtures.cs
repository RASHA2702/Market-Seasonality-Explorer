using FiatCryptoGateway.Domain.Assets;
using FiatCryptoGateway.Domain.Compliance;
using FiatCryptoGateway.Domain.Orders;
using FiatCryptoGateway.Domain.Pricing;
using FiatCryptoGateway.Sandbox;

namespace FiatCryptoGateway.Tests;

public sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

internal static class Fixtures
{
    public const string ClientId = "client-a";
    public const string Address = "0x1111111111111111111111111111111111111111";

    public static readonly DateTimeOffset Start = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
    public static readonly AssetCatalog Catalog = SandboxAssets.CreateCatalog();

    public static Asset Inr => Catalog.Get(AssetId.Fiat("INR"));

    public static Asset Usd => Catalog.Get(AssetId.Fiat("USD"));

    public static Asset Jpy => Catalog.Get(AssetId.Fiat("JPY"));

    public static Asset Usdc => Catalog.Get(SandboxAssets.UsdcEthereum);

    public static Asset Ether => Catalog.Get(SandboxAssets.Ether);

    public static PricingPolicy Policy => new(
        "test-1",
        ServiceFeeBasisPoints: 150,
        SpreadBasisPoints: 50,
        QuoteTimeToLive: TimeSpan.FromSeconds(60),
        PaymentWindow: TimeSpan.FromMinutes(15),
        MaximumPriceAge: TimeSpan.FromSeconds(30),
        PivotCurrency: Usd,
        MinimumPivotAmount: 10m,
        MaximumPivotAmount: 10_000m);

    /// <summary>INR to USD at 0.0118, then USD to USDC at 1.</summary>
    public static IReadOnlyList<PriceObservation> InrToUsdc(DateTimeOffset observedAt, DateTimeOffset? availableAt = null) =>
    [
        new(Inr.Id, Usd.Id, 0.0118m, observedAt, availableAt ?? observedAt, "test"),
        new(Usd.Id, Usdc.Id, 1m, observedAt, availableAt ?? observedAt, "test"),
    ];

    public static IReadOnlyList<PriceObservation> InrToUsd(DateTimeOffset at) => [new(Inr.Id, Usd.Id, 0.0118m, at, at, "test")];

    public static QuoteInputs Inputs(decimal inr = 10_000m, decimal networkFee = 1.5m) =>
        new(ClientId, new Money(inr, Inr), Usdc, InrToUsdc(Start), InrToUsd(Start), new Money(networkFee, Usdc), Policy, Start);

    public static Quote Quote(decimal inr = 10_000m) => QuoteCalculator.Calculate(Inputs(inr));

    public static PaymentOrder Order(Quote? quote = null) =>
        PaymentOrder.Create(Guid.NewGuid(), ClientId, "cust_verified", quote ?? Quote(), Address, Start, TimeSpan.FromMinutes(15));

    public static FiatPaymentConfirmation Payment(PaymentOrder order, Money? amount = null, DateTimeOffset? capturedAt = null) =>
        new("test-psp", "pay_1", order.OrderId, amount ?? order.Quote.SourceAmount, capturedAt ?? Start.AddMinutes(1));

    public static ComplianceDecision Decision(PaymentOrder order, ComplianceOutcome outcome = ComplianceOutcome.Approved) =>
        new(order.OrderId, outcome, [], "test", Start.AddMinutes(1));

    /// <summary>The provider buys at the market rate: 9,850 INR x 0.0118 = 116.23 USDC.</summary>
    public static LiquidityExecution Execution(PaymentOrder order) =>
        new("test-lp", "lpx_1", order.OrderId, order.Quote.NetSourceAmount, new Money(116.23m, Usdc), Start.AddMinutes(2));

    public static CryptoTransfer Transfer(PaymentOrder order, string? address = null) =>
        new("test-custody", "tx_1", order.OrderId, order.Quote.DestinationAmount, address ?? Address, new Money(0.0004m, Ether), Start.AddMinutes(3));
}
