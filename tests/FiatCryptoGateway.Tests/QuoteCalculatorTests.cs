using FiatCryptoGateway.Domain;
using FiatCryptoGateway.Domain.Assets;
using FiatCryptoGateway.Domain.Pricing;

namespace FiatCryptoGateway.Tests;

public sealed class QuoteCalculatorTests
{
    [Fact]
    public void Quote_AppliesFeeSpreadAndNetworkFeeInOrder()
    {
        var quote = Fixtures.Quote(10_000m);

        Assert.Equal("10000.00", quote.SourceAmount.ToFixedString());
        Assert.Equal("150.00", quote.ServiceFee.ToFixedString());         // 1.5% of 10,000 INR
        Assert.Equal("9850.00", quote.NetSourceAmount.ToFixedString());
        Assert.Equal(0.0118m, quote.MarketRate);
        Assert.Equal(0.011741m, quote.CustomerRate);                      // 0.5% spread
        Assert.Equal("115.648850", quote.GrossDestinationAmount.ToFixedString()); // 9,850 x 0.011741
        Assert.Equal("1.500000", quote.NetworkFee.ToFixedString());
        Assert.Equal("114.148850", quote.DestinationAmount.ToFixedString());
        Assert.Equal("118.00", quote.PivotAmount.ToFixedString());
        Assert.Equal(Fixtures.Start.AddSeconds(60), quote.ExpiresAt);
    }

    [Fact]
    public void Quote_FiguresAlwaysAddUp()
    {
        foreach (var amount in new[] { 1_234.57m, 5_000m, 99_999.99m, 847_457.62m })
        {
            var quote = Fixtures.Quote(amount);

            Assert.Equal(quote.SourceAmount, quote.ServiceFee.Add(quote.NetSourceAmount));
            Assert.Equal(quote.GrossDestinationAmount, quote.DestinationAmount.Add(quote.NetworkFee));
        }
    }

    [Fact]
    public void ServiceFee_RoundsUpAndCryptoRoundsDown()
    {
        var quote = Fixtures.Quote(1_234.57m);

        Assert.Equal("18.52", quote.ServiceFee.ToFixedString());                 // 18.51855 rounded up
        Assert.Equal("14.277643", quote.GrossDestinationAmount.ToFixedString()); // 14.27764305 rounded down
    }

    [Fact]
    public void ZeroDecimalCurrency_FeeRoundsUpToAWholeUnit()
    {
        var inputs = Fixtures.Inputs() with
        {
            SourceAmount = new Money(123_457m, Fixtures.Jpy),
            PricePath =
            [
                new(Fixtures.Jpy.Id, Fixtures.Usd.Id, 0.0066m, Fixtures.Start, Fixtures.Start, "test"),
                new(Fixtures.Usd.Id, Fixtures.Usdc.Id, 1m, Fixtures.Start, Fixtures.Start, "test"),
            ],
            PivotRoute = [new(Fixtures.Jpy.Id, Fixtures.Usd.Id, 0.0066m, Fixtures.Start, Fixtures.Start, "test")],
        };

        var quote = QuoteCalculator.Calculate(inputs);

        Assert.Equal("1852", quote.ServiceFee.ToFixedString()); // 1,851.855 yen rounded up
    }

    [Fact]
    public void StalePrice_IsRefused()
    {
        var inputs = Fixtures.Inputs() with { PricePath = Fixtures.InrToUsdc(Fixtures.Start.AddSeconds(-31)) };

        AssertRefused("PRICE_STALE", inputs);
    }

    [Fact]
    public void PriceNotYetAvailable_IsRefused()
    {
        var inputs = Fixtures.Inputs() with
        {
            PricePath = Fixtures.InrToUsdc(Fixtures.Start.AddSeconds(-1), availableAt: Fixtures.Start.AddSeconds(1)),
        };

        AssertRefused("PRICE_NOT_YET_AVAILABLE", inputs);
    }

    [Fact]
    public void BrokenPriceRoute_IsRefused()
    {
        var eur = Fixtures.Catalog.Get(AssetId.Fiat("EUR"));
        var inputs = Fixtures.Inputs() with
        {
            PricePath =
            [
                new(Fixtures.Inr.Id, Fixtures.Usd.Id, 0.0118m, Fixtures.Start, Fixtures.Start, "test"),
                new(eur.Id, Fixtures.Usdc.Id, 1.08m, Fixtures.Start, Fixtures.Start, "test"),
            ],
        };

        AssertRefused("PRICE_ROUTE_BROKEN", inputs);
    }

    [Theory]
    [InlineData("500", "BELOW_MINIMUM_AMOUNT")]       // 5.90 USD
    [InlineData("1000000", "ABOVE_MAXIMUM_AMOUNT")]   // 11,800 USD
    public void OrderLimits_ApplyInThePivotCurrency(string inr, string code)
    {
        AssertRefused(code, Fixtures.Inputs(decimal.Parse(inr, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void AmountThatDoesNotCoverTheNetworkFee_IsRefused()
    {
        AssertRefused("AMOUNT_TOO_SMALL_FOR_NETWORK_FEE", Fixtures.Inputs(networkFee: 200m));
    }

    [Fact]
    public void CryptoAsSource_IsRefused()
    {
        var inputs = Fixtures.Inputs() with { SourceAmount = new Money(10m, Fixtures.Usdc) };

        AssertRefused("SOURCE_MUST_BE_FIAT", inputs);
    }

    [Fact]
    public void QuoteExpires_AfterItsTimeToLive()
    {
        var quote = Fixtures.Quote();

        Assert.False(quote.IsExpiredAt(Fixtures.Start.AddSeconds(59)));
        Assert.True(quote.IsExpiredAt(Fixtures.Start.AddSeconds(60)));
    }

    private static void AssertRefused(string code, QuoteInputs inputs)
    {
        var exception = Assert.Throws<GatewayRuleException>(() => QuoteCalculator.Calculate(inputs));
        Assert.Equal(code, exception.Code);
    }
}
