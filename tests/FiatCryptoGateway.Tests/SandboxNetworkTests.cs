using FiatCryptoGateway.Application;
using FiatCryptoGateway.Application.OnRamp;
using FiatCryptoGateway.Domain.Assets;
using FiatCryptoGateway.Sandbox;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FiatCryptoGateway.Tests;

/// <summary>The stablecoins and networks chosen in question 13 work end to end in the sandbox.</summary>
public sealed class SandboxNetworkTests
{
    public static TheoryData<string> Stablecoins => new(SandboxAssets.SettlementStablecoins.Select(asset => asset.ToString()));

    [Theory]
    [MemberData(nameof(Stablecoins))]
    public async Task EachChosenStablecoin_CanBeQuotedFromInr(string asset)
    {
        await using var services = BuildServices();

        var quote = await services.GetRequiredService<OnRampService>()
            .CreateQuoteAsync(Fixtures.ClientId, new CreateQuoteCommand("INR", 10_000m, asset), CancellationToken.None);

        Assert.Equal(asset, quote.DestinationAmount.Asset.Id.ToString());
        Assert.Equal(6, quote.DestinationAmount.Asset.Scale);
        Assert.True(quote.DestinationAmount.IsPositive);
        Assert.Equal(quote.DestinationAmount.Asset.Id, quote.NetworkFee.Asset.Id); // the fee is charged in the stablecoin
    }

    [Theory]
    [InlineData("USDT@tron", "TRX@tron")]
    [InlineData("USDT@polygon", "POL@polygon")]
    [InlineData("USDC@polygon", "POL@polygon")]
    [InlineData("USDT@solana", "SOL@solana")]
    public void NewStablecoins_PayNetworkFeesInTheirNetworksCoin(string asset, string feeAsset)
    {
        Assert.Equal(AssetId.Parse(feeAsset), Fixtures.Catalog.Get(AssetId.Parse(asset)).NetworkFeeAsset);
    }

    [Theory]
    [InlineData("tron", "TLyqzVGLV1srkB7dToTAEqgDSfPtXRJZYH", true)]
    [InlineData("tron", "0x1111111111111111111111111111111111111111", false)]
    [InlineData("tron", "TLyqzVGLV1srkB7dToTAEqgDSfPtXRJZY", false)]   // 33 characters
    [InlineData("tron", "TLyqzVGLV1srkB7dToTAEqgDSfPtXRJZY0", false)]  // 0 is not base58
    [InlineData("polygon", "0x1111111111111111111111111111111111111111", true)]
    [InlineData("polygon", "TLyqzVGLV1srkB7dToTAEqgDSfPtXRJZYH", false)]
    [InlineData("ethereum", "0x111111111111111111111111111111111111111", false)] // 39 hex digits
    public void Addresses_AreCheckedInEachNetworksFormat(string network, string address, bool valid)
    {
        var custody = new SandboxCustodyProvider(Fixtures.Catalog, TimeProvider.System);
        var stablecoin = SandboxAssets.SettlementStablecoins.First(asset => asset.Network == network);

        Assert.Equal(valid, custody.IsValidAddress(Fixtures.Catalog.Get(stablecoin), address));
    }

    private static ServiceProvider BuildServices()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Gateway:Fiat:Provider"] = "SandboxPartner" })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new ManualTimeProvider(Fixtures.Start));
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddGatewayCore(configuration);
        services.AddSandboxProviders(configuration);
        services.AddInMemoryStores();
        return services.BuildServiceProvider(validateScopes: true);
    }
}
