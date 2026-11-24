using FiatCryptoGateway.Domain;
using FiatCryptoGateway.Domain.Assets;

namespace FiatCryptoGateway.Tests;

public sealed class AssetAndMoneyTests
{
    [Fact]
    public void Iso4217_ContainsEveryCirculatingCurrency()
    {
        Assert.Equal(156, Iso4217.All.Count);
        Assert.Equal(Iso4217.All.Count, Iso4217.All.Select(currency => currency.Id).Distinct().Count());
    }

    [Theory]
    [InlineData("USD", 2)]
    [InlineData("EUR", 2)]
    [InlineData("INR", 2)]
    [InlineData("JPY", 0)]
    [InlineData("KRW", 0)]
    [InlineData("CLP", 0)]
    [InlineData("KWD", 3)]
    [InlineData("BHD", 3)]
    [InlineData("XOF", 0)]
    [InlineData("XCG", 2)]
    public void Iso4217_UsesOfficialMinorUnits(string code, int minorUnits)
    {
        Assert.Equal(minorUnits, Fixtures.Catalog.Get(AssetId.Fiat(code)).Scale);
    }

    [Theory]
    [InlineData("XAU")] // gold
    [InlineData("XXX")] // no currency
    [InlineData("XTS")] // testing code
    [InlineData("USN")] // fund code
    [InlineData("BGN")] // replaced by the euro on 2026-01-01
    public void Iso4217_ExcludesNonCirculatingCodes(string code)
    {
        Assert.False(Fixtures.Catalog.TryGet(AssetId.Fiat(code), out _));
    }

    [Fact]
    public void CryptoAssets_AreIdentifiedBySymbolAndNetwork()
    {
        Assert.NotEqual(AssetId.Crypto("USDC", "ethereum"), AssetId.Crypto("USDC", "solana"));
        Assert.Equal(AssetId.Crypto("USDC", "ethereum"), AssetId.Parse("USDC@ethereum"));
        Assert.Equal(AssetId.Fiat("INR"), AssetId.Parse("INR"));
        Assert.Equal("USDC@ethereum", AssetId.Parse("USDC@ethereum").ToString());
    }

    [Theory]
    [InlineData("usd")]
    [InlineData("US")]
    [InlineData("USDC@")]
    [InlineData("@ethereum")]
    [InlineData("USDC@Ethereum")]
    public void MalformedAssetIds_AreRejected(string value)
    {
        var exception = Assert.Throws<GatewayRuleException>(() => AssetId.Parse(value));
        Assert.Equal("INVALID_ASSET", exception.Code);
    }

    [Theory]
    [InlineData("USD", "1.001")]
    [InlineData("JPY", "0.5")]
    [InlineData("KWD", "1.0001")]
    public void Money_RefusesMorePrecisionThanTheCurrencyHas(string code, string amount)
    {
        var exception = Assert.Throws<GatewayRuleException>(() =>
            new Money(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), Fixtures.Catalog.Get(AssetId.Fiat(code))));
        Assert.Equal("AMOUNT_PRECISION", exception.Code);
    }

    [Theory]
    [InlineData("USD", "1.001", "1.01", "1.00")]
    [InlineData("JPY", "100.4", "101", "100")]
    [InlineData("KWD", "2.00051", "2.001", "2.000")]
    public void Rounding_IsDirectional(string code, string raw, string up, string down)
    {
        var asset = Fixtures.Catalog.Get(AssetId.Fiat(code));
        var value = decimal.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(up, Money.RoundUp(value, asset).ToFixedString());
        Assert.Equal(down, Money.RoundDown(value, asset).ToFixedString());
    }

    [Fact]
    public void Money_InDifferentAssetsCannotBeCombined()
    {
        var exception = Assert.Throws<GatewayRuleException>(() => new Money(1m, Fixtures.Usd).Add(new Money(1m, Fixtures.Inr)));
        Assert.Equal("ASSET_MISMATCH", exception.Code);
    }

    [Fact]
    public void FixedString_UsesTheAssetScale()
    {
        Assert.Equal("5.000000", new Money(5m, Fixtures.Usdc).ToFixedString());
        Assert.Equal("0.000400000000000000", new Money(0.0004m, Fixtures.Ether).ToFixedString());
    }
}
