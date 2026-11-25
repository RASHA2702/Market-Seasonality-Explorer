using FiatCryptoGateway.Application;
using FiatCryptoGateway.Application.Ports;
using FiatCryptoGateway.Application.Settlement;
using FiatCryptoGateway.Domain;
using FiatCryptoGateway.Domain.Compliance;
using FiatCryptoGateway.Domain.Settlement;
using FiatCryptoGateway.Sandbox;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FiatCryptoGateway.Tests;

public sealed class MerchantSettlementTests
{
    private const string Polygon = "polygon";
    private const string WalletA = "0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string WalletB = "0xbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly DateTimeOffset T0 = Fixtures.Start;
    private static readonly TimeSpan CoolingOff = TimeSpan.FromHours(24);
    private static readonly WalletScreeningResult Clean = new(WalletRisk.Low, "test", null);

    [Fact]
    public void CleanWallet_IsUsableOnlyAfterTheCoolingOffPeriod()
    {
        var settlement = MerchantSettlement.Empty(Fixtures.ClientId);

        var wallet = Register(settlement, WalletA, Clean, T0);

        Assert.Equal(SettlementWalletStatus.Approved, wallet.Status);
        Assert.Null(settlement.Current(Polygon, T0.AddHours(24).AddTicks(-1)));
        Assert.Same(wallet, settlement.Current(Polygon, T0.AddHours(24)));
        Assert.Equal(("CoolingOff", "Active"), (SettlementWalletView.Status(wallet, T0), SettlementWalletView.Status(wallet, T0.AddHours(24))));
    }

    [Theory]
    [InlineData(WalletRisk.High)]
    [InlineData(WalletRisk.Unknown)] // screening could not finish
    [InlineData(WalletRisk.Blocked)]
    public void RiskyOrUnscreenedWallet_WaitsForAReviewer(WalletRisk risk)
    {
        var settlement = MerchantSettlement.Empty(Fixtures.ClientId);

        var wallet = Register(settlement, WalletA, new WalletScreeningResult(risk, "test", null), T0);

        Assert.Equal(SettlementWalletStatus.UnderReview, wallet.Status);
        Assert.Null(settlement.Current(Polygon, T0.AddDays(30)));
        Assert.Equal("UnderReview", SettlementWalletView.Status(wallet, T0.AddDays(30)));
    }

    [Fact]
    public void ApprovedWallet_StillWaitsOutItsCoolingOffPeriod_AndKeepsWhoDecidedAndWhy()
    {
        var settlement = MerchantSettlement.Empty(Fixtures.ClientId);
        var wallet = Register(settlement, WalletA, new WalletScreeningResult(WalletRisk.High, "test", "case-1"), T0);

        settlement.Approve(wallet.WalletId, "officer", "Owner verified by phone.", T0.AddHours(1));

        Assert.Null(settlement.Current(Polygon, T0.AddHours(2)));
        Assert.Same(wallet, settlement.Current(Polygon, T0.AddHours(24)));
        Assert.Equal(
            [
                (SettlementWalletActionKind.Registered, Fixtures.ClientId, (string?)null),
                (SettlementWalletActionKind.Approved, "officer", "Owner verified by phone."),
            ],
            wallet.History.Select(action => (action.Kind, action.Actor, action.Reason)));
    }

    [Fact]
    public void BlockedWallet_CanNeverBeApproved()
    {
        var settlement = MerchantSettlement.Empty(Fixtures.ClientId);
        var wallet = Register(settlement, WalletA, new WalletScreeningResult(WalletRisk.Blocked, "test", null), T0);

        var exception = Assert.Throws<GatewayRuleException>(() => settlement.Approve(wallet.WalletId, "officer", "Looks fine.", T0));

        Assert.Equal("WALLET_BLOCKED", exception.Code);
        settlement.Reject(wallet.WalletId, "officer", "On a sanctions list.", T0);
        Assert.Equal("NotAccepted", SettlementWalletView.Status(wallet, T0));
    }

    [Fact]
    public void ReviewDecisions_NeedAReason_AndApplyOnlyToWalletsUnderReview()
    {
        var settlement = MerchantSettlement.Empty(Fixtures.ClientId);
        var risky = Register(settlement, WalletA, new WalletScreeningResult(WalletRisk.High, "test", null), T0);
        var clean = Register(settlement, WalletB, Clean, T0, network: "tron-test");

        Assert.Equal("REASON_REQUIRED", Assert.Throws<GatewayRuleException>(() => settlement.Approve(risky.WalletId, "officer", " ", T0)).Code);
        Assert.Equal("WALLET_NOT_UNDER_REVIEW", Assert.Throws<GatewayRuleException>(() => settlement.Reject(clean.WalletId, "officer", "No.", T0)).Code);
    }

    [Fact]
    public void Replacement_TakesOverOnlyWhenItBecomesUsable()
    {
        var settlement = MerchantSettlement.Empty(Fixtures.ClientId);
        var first = Register(settlement, WalletA, Clean, T0);
        var second = Register(settlement, WalletB, Clean, T0.AddHours(48)); // usable from 72 h

        Assert.Same(first, settlement.Current(Polygon, T0.AddHours(72).AddTicks(-1)));
        Assert.Same(second, settlement.Current(Polygon, T0.AddHours(72)));
        Assert.Equal("Replaced", SettlementWalletView.Status(first, T0.AddHours(72)));
    }

    [Fact]
    public void WithdrawingAWaitingReplacement_KeepsTheWalletInUse()
    {
        var settlement = MerchantSettlement.Empty(Fixtures.ClientId);
        var first = Register(settlement, WalletA, Clean, T0);
        var second = Register(settlement, WalletB, Clean, T0.AddHours(48));

        settlement.Disable(second.WalletId, Fixtures.ClientId, null, T0.AddHours(50));

        Assert.Same(first, settlement.Current(Polygon, T0.AddDays(10)));
        Assert.Null(first.RetiredAt);
    }

    [Fact]
    public void WithdrawingTheWalletInUse_LeavesTheNetworkWithoutOne()
    {
        var settlement = MerchantSettlement.Empty(Fixtures.ClientId);
        var first = Register(settlement, WalletA, Clean, T0);
        var second = Register(settlement, WalletB, Clean, T0.AddHours(48));

        settlement.Disable(second.WalletId, Fixtures.ClientId, "Lost the keys.", T0.AddHours(80)); // after it took over

        Assert.Null(settlement.Current(Polygon, T0.AddHours(81)));
        Assert.Equal("Replaced", SettlementWalletView.Status(first, T0.AddHours(81))); // the old wallet does not come back
    }

    [Fact]
    public void ApprovingAReplacementUnderReview_RetiresTheOldWalletWhenTheNewOneIsUsable()
    {
        var settlement = MerchantSettlement.Empty(Fixtures.ClientId);
        var first = Register(settlement, WalletA, Clean, T0);
        var second = Register(settlement, WalletB, new WalletScreeningResult(WalletRisk.High, "test", null), T0.AddHours(48));
        Assert.Same(first, settlement.Current(Polygon, T0.AddHours(79))); // the review takes a while

        settlement.Approve(second.WalletId, "officer", "Checked.", T0.AddHours(80)); // after its cooling-off period

        Assert.Same(second, settlement.Current(Polygon, T0.AddHours(80)));
        Assert.Equal(T0.AddHours(80), first.RetiredAt);
    }

    [Fact]
    public void OnlyOneChangePerNetwork_CanWait()
    {
        var settlement = MerchantSettlement.Empty(Fixtures.ClientId);
        Register(settlement, WalletA, Clean, T0);

        var exception = Assert.Throws<GatewayRuleException>(() => Register(settlement, WalletB, Clean, T0.AddHours(1)));

        Assert.Equal("WALLET_CHANGE_PENDING", exception.Code);
        Register(settlement, WalletB, Clean, T0.AddHours(1), network: "ethereum"); // other networks are independent
    }

    [Fact]
    public void RegisteringTheSameWalletAgain_ReturnsIt_UntilItIsWithdrawn()
    {
        var settlement = MerchantSettlement.Empty(Fixtures.ClientId);
        var wallet = Register(settlement, WalletA, Clean, T0);

        var again = settlement.Register(Guid.NewGuid(), Polygon, WalletA, null, Clean, Fixtures.ClientId, T0.AddHours(30), CoolingOff, out var created);

        Assert.False(created);
        Assert.Same(wallet, again);
        Assert.Single(wallet.History);
        settlement.Disable(wallet.WalletId, Fixtures.ClientId, null, T0.AddHours(31));
        var fresh = settlement.Register(Guid.NewGuid(), Polygon, WalletA, null, Clean, Fixtures.ClientId, T0.AddHours(32), CoolingOff, out created);
        Assert.True(created);
        Assert.NotEqual(wallet.WalletId, fresh.WalletId);
    }

    [Fact]
    public void Copy_IsIndependentOfTheOriginal()
    {
        var settlement = MerchantSettlement.Empty(Fixtures.ClientId);
        var wallet = Register(settlement, WalletA, Clean, T0);

        var copy = settlement.Copy();
        copy.Disable(wallet.WalletId, Fixtures.ClientId, null, T0.AddHours(1));

        Assert.Equal(SettlementWalletStatus.Approved, wallet.Status);
        Assert.Equal(SettlementWalletStatus.Disabled, copy.Find(wallet.WalletId)!.Status);
    }

    private static SettlementWallet Register(
        MerchantSettlement settlement,
        string address,
        WalletScreeningResult screening,
        DateTimeOffset at,
        string network = Polygon)
    {
        var wallet = settlement.Register(Guid.NewGuid(), network, address, null, screening, Fixtures.ClientId, at, CoolingOff, out var created);
        Assert.True(created);
        return wallet;
    }
}

public sealed class SettlementServiceTests
{
    private const string Wallet = "0x2222222222222222222222222222222222222222";
    private static readonly CancellationToken None = CancellationToken.None;

    [Theory]
    [InlineData("bitcoin", "bc1qxy2kgdygjrsqtzq2n0yrf2493p83kkfjhx0wlh", "UNSUPPORTED_NETWORK")] // not a settlement network
    [InlineData("tron", "0x2222222222222222222222222222222222222222", "INVALID_WALLET_ADDRESS")]
    [InlineData("polygon", "", "INVALID_WALLET_ADDRESS")]
    public async Task Registration_ChecksTheNetworkAndTheAddressFormat(string network, string address, string code)
    {
        await using var services = BuildServices(new ManualTimeProvider(Fixtures.Start));

        var exception = await Assert.ThrowsAsync<GatewayRuleException>(() =>
            services.GetRequiredService<SettlementService>().RegisterAsync(Fixtures.ClientId, network, address, null, None));

        Assert.Equal(code, exception.Code);
    }

    [Theory]
    [InlineData(Wallet, SettlementWalletStatus.Approved, WalletRisk.Low)]
    [InlineData(SandboxWalletScreening.HighRiskAddress, SettlementWalletStatus.UnderReview, WalletRisk.High)]
    [InlineData(SandboxWalletScreening.BlockedAddress, SettlementWalletStatus.UnderReview, WalletRisk.Blocked)]
    [InlineData(SandboxWalletScreening.OutageAddress, SettlementWalletStatus.UnderReview, WalletRisk.Unknown)] // fails closed
    public async Task Screening_DecidesWhetherAWalletNeedsAReviewer(string address, SettlementWalletStatus status, WalletRisk risk)
    {
        await using var services = BuildServices(new ManualTimeProvider(Fixtures.Start));

        var (wallet, created) = await services.GetRequiredService<SettlementService>().RegisterAsync(Fixtures.ClientId, "Ethereum", address, "Treasury", None);

        Assert.True(created);
        Assert.Equal((status, risk, "ethereum", "Treasury"), (wallet.Status, wallet.Screening.Risk, wallet.Network, wallet.Label));
    }

    [Fact]
    public async Task ReviewerApproval_MakesARiskyWalletUsableAfterItsCoolingOffPeriod()
    {
        var time = new ManualTimeProvider(Fixtures.Start);
        await using var services = BuildServices(time);
        var settlement = services.GetRequiredService<SettlementService>();
        var (wallet, _) = await settlement.RegisterAsync(Fixtures.ClientId, "ethereum", SandboxWalletScreening.HighRiskAddress, null, None);

        Assert.Equal(wallet.WalletId, Assert.Single(await settlement.ListForReviewAsync(None)).WalletId);
        await settlement.ApproveAsync(wallet.WalletId, "officer", "Merchant's own treasury, verified.", None);

        Assert.Empty(await settlement.ListForReviewAsync(None));
        Assert.Null(await settlement.CurrentAsync(Fixtures.ClientId, "ethereum", None));
        time.Advance(TimeSpan.FromHours(24));
        Assert.Equal(wallet.WalletId, (await settlement.CurrentAsync(Fixtures.ClientId, "ethereum", None))!.WalletId);
    }

    [Fact]
    public async Task Merchants_CannotSeeOrChangeEachOthersWallets()
    {
        await using var services = BuildServices(new ManualTimeProvider(Fixtures.Start));
        var settlement = services.GetRequiredService<SettlementService>();
        var (wallet, _) = await settlement.RegisterAsync(Fixtures.ClientId, "polygon", Wallet, null, None);

        await Assert.ThrowsAsync<ResourceNotFoundException>(() => settlement.DisableByMerchantAsync("client-b", wallet.WalletId, None));

        Assert.Empty(await settlement.ListAsync("client-b", None));
        Assert.Equal(SettlementWalletStatus.Approved, Assert.Single(await settlement.ListAsync(Fixtures.ClientId, None)).Status);
    }

    [Fact]
    public async Task ReviewerDisabling_NeedsAReason()
    {
        await using var services = BuildServices(new ManualTimeProvider(Fixtures.Start));
        var settlement = services.GetRequiredService<SettlementService>();
        var (wallet, _) = await settlement.RegisterAsync(Fixtures.ClientId, "polygon", Wallet, null, None);

        var exception = await Assert.ThrowsAsync<GatewayRuleException>(() => settlement.DisableByReviewerAsync(wallet.WalletId, "officer", null, None));

        Assert.Equal("REASON_REQUIRED", exception.Code);
        var disabled = await settlement.DisableByReviewerAsync(wallet.WalletId, "officer", "Linked to a fraud report.", None);
        Assert.Equal(("officer", "Linked to a fraud report."), (disabled.History[^1].Actor, disabled.History[^1].Reason));
    }

    [Fact]
    public async Task StaleCopy_CannotOverwriteNewerSettings()
    {
        var store = new InMemorySettlementStore();
        var first = await store.GetAsync(Fixtures.ClientId, None);
        var second = await store.GetAsync(Fixtures.ClientId, None);
        first.Register(Guid.NewGuid(), "polygon", Wallet, null, new WalletScreeningResult(WalletRisk.Low, "test", null), Fixtures.ClientId, Fixtures.Start, TimeSpan.Zero, out _);
        await store.SaveAsync(first, None);
        second.Register(Guid.NewGuid(), "tron", "TLyqzVGLV1srkB7dToTAEqgDSfPtXRJZYH", null, new WalletScreeningResult(WalletRisk.Low, "test", null), Fixtures.ClientId, Fixtures.Start, TimeSpan.Zero, out _);

        await Assert.ThrowsAsync<SettlementConcurrencyException>(() => store.SaveAsync(second, None));
    }

    [Theory]
    [InlineData("CoolingOffHours", "-1")]
    [InlineData("CoolingOffHours", "721")]
    [InlineData("Networks", "polygon,mars")]
    [InlineData("Networks", " ")]
    public void InvalidSettlementSettings_AreRefused(string key, string value)
    {
        var options = new SettlementOptions();
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [key] = value }).Build().Bind(options);

        Assert.True(new SettlementOptionsValidator(Fixtures.Catalog).Validate(null, options).Failed);
        Assert.True(new SettlementOptionsValidator(Fixtures.Catalog).Validate(null, new SettlementOptions()).Succeeded);
    }

    private static ServiceProvider BuildServices(TimeProvider time)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
        var services = new ServiceCollection();
        services.AddSingleton(time);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddGatewayCore(configuration);
        services.AddSandboxProviders(configuration);
        services.AddInMemoryStores();
        return services.BuildServiceProvider(validateScopes: true);
    }
}
