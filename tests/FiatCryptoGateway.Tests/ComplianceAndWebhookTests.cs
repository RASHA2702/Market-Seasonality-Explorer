using FiatCryptoGateway.Application.Configuration;
using FiatCryptoGateway.Application.Webhooks;
using FiatCryptoGateway.Domain.Assets;
using FiatCryptoGateway.Domain.Compliance;
using Microsoft.Extensions.Configuration;

namespace FiatCryptoGateway.Tests;

public sealed class ComplianceRulesTests
{
    private static readonly CompliancePolicy Policy = new(
        CountryPolicyMode.AllowList,
        new HashSet<string>(["IN"], StringComparer.Ordinal),
        Basic: new KycLimits(1_000m, 2_000m, 5_000m),
        Full: new KycLimits(10_000m, 20_000m, 50_000m),
        ManualReviewThreshold: 5_000m);

    [Fact]
    public void CleanVerifiedCustomer_IsApproved()
    {
        var (outcome, reasons) = Evaluate(Customer());

        Assert.Equal(ComplianceOutcome.Approved, outcome);
        Assert.Empty(reasons);
    }

    [Theory]
    [InlineData(KycLevel.None, "IN", false, 100, "KYC_REQUIRED")]
    [InlineData(KycLevel.Basic, "IN", false, 1_500, "KYC_LIMIT_EXCEEDED")]
    [InlineData(KycLevel.Full, "KP", false, 100, "COUNTRY_NOT_SUPPORTED")]
    [InlineData(KycLevel.Full, "IN", true, 100, "CUSTOMER_SANCTIONS_MATCH")]
    public void CustomerProblems_Reject(KycLevel level, string country, bool sanctions, int amount, string reason)
    {
        var (outcome, reasons) = Evaluate(Customer(level, country, sanctions), pivotAmount: amount);

        Assert.Equal(ComplianceOutcome.Rejected, outcome);
        Assert.Contains(reason, reasons);
    }

    [Theory]
    [InlineData(WalletRisk.Blocked, ComplianceOutcome.Rejected, "WALLET_BLOCKED")]
    [InlineData(WalletRisk.High, ComplianceOutcome.ManualReview, "WALLET_HIGH_RISK")]
    [InlineData(WalletRisk.Unknown, ComplianceOutcome.ManualReview, "WALLET_RISK_UNKNOWN")]
    public void WalletRisk_IsNeverAssumedLow(WalletRisk risk, ComplianceOutcome expected, string reason)
    {
        var (outcome, reasons) = Evaluate(Customer(), risk);

        Assert.Equal(expected, outcome);
        Assert.Contains(reason, reasons);
    }

    [Fact]
    public void LargeOrder_NeedsAPerson()
    {
        var (outcome, reasons) = Evaluate(Customer(), pivotAmount: 5_000);

        Assert.Equal(ComplianceOutcome.ManualReview, outcome);
        Assert.Contains("AMOUNT_REQUIRES_REVIEW", reasons);
    }

    [Theory]
    [InlineData(1_500, 0, 600, "DAILY_LIMIT_EXCEEDED")]     // 1,500 today + 600 > 2,000
    [InlineData(0, 4_800, 300, "MONTHLY_LIMIT_EXCEEDED")]   // 4,800 this month + 300 > 5,000
    public void DailyAndMonthlyTotals_AreEnforced(int lastDay, int lastMonth, int amount, string reason)
    {
        var (outcome, reasons) = Evaluate(Customer(KycLevel.Basic), pivotAmount: amount, volume: new CustomerVolume(lastDay, Math.Max(lastDay, lastMonth)));

        Assert.Equal(ComplianceOutcome.Rejected, outcome);
        Assert.Equal([reason], reasons);
    }

    [Fact]
    public void TotalsExactlyAtTheLimit_AreAllowed()
    {
        var (outcome, _) = Evaluate(Customer(KycLevel.Basic), pivotAmount: 500, volume: new CustomerVolume(1_500m, 4_500m));

        Assert.Equal(ComplianceOutcome.Approved, outcome);
    }

    [Theory]
    [InlineData(CountryPolicyMode.AllowList, "IN", "IN", true)]
    [InlineData(CountryPolicyMode.AllowList, "IN", "GB", false)]
    [InlineData(CountryPolicyMode.BlockList, "KP", "GB", true)]
    [InlineData(CountryPolicyMode.BlockList, "KP", "KP", false)]
    public void CountryRule_SupportsAllowAndBlockLists(CountryPolicyMode mode, string listed, string customerCountry, bool allowed)
    {
        var policy = Policy with { CountryMode = mode, Countries = new HashSet<string>([listed], StringComparer.Ordinal) };

        var (_, reasons) = ComplianceRules.Evaluate(
            Customer(country: customerCountry), new WalletScreeningResult(WalletRisk.Low, "test", null), new Money(100m, Fixtures.Usd), CustomerVolume.None, policy);

        Assert.Equal(!allowed, reasons.Contains("COUNTRY_NOT_SUPPORTED"));
    }

    [Fact]
    public void RejectionOutranksReview()
    {
        var (outcome, _) = Evaluate(Customer(KycLevel.None), WalletRisk.High, pivotAmount: 6_000);

        Assert.Equal(ComplianceOutcome.Rejected, outcome);
    }

    private static CustomerProfile Customer(KycLevel level = KycLevel.Full, string country = "IN", bool sanctions = false) =>
        new("client-a", "cust", level, country, sanctions);

    private static (ComplianceOutcome Outcome, IReadOnlyList<string> Reasons) Evaluate(
        CustomerProfile customer,
        WalletRisk risk = WalletRisk.Low,
        int pivotAmount = 100,
        CustomerVolume? volume = null) =>
        ComplianceRules.Evaluate(
            customer, new WalletScreeningResult(risk, "test", null), new Money(pivotAmount, Fixtures.Usd), volume ?? CustomerVolume.None, Policy);
}

public sealed class ComplianceOptionsTests
{
    [Fact]
    public void DefaultCountryRule_IsIndiaOnly()
    {
        var options = Bind([]);

        Assert.Equal(CountryPolicyMode.AllowList, options.CountryMode);
        Assert.Equal(["IN"], options.EffectiveCountries);
    }

    [Fact]
    public void ConfiguredCountries_ReplaceTheDefaultInsteadOfAddingToIt()
    {
        var options = Bind(new() { ["Countries"] = "ae, in" });

        Assert.Equal(["AE", "IN"], options.EffectiveCountries.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void BlockListWithoutCountries_UsesThePlaceholderList()
    {
        var options = Bind(new() { ["CountryMode"] = "BlockList" });

        Assert.Equal(["CU", "IR", "KP", "SY"], options.EffectiveCountries.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("Countries", "IND")]
    [InlineData("BasicKycDailyLimit", "500")]   // below the per-payment limit
    [InlineData("FullKycMonthlyLimit", "1000")] // below the daily limit
    public void InvalidSettings_AreRefusedAtStartup(string key, string value)
    {
        var result = new ComplianceOptionsValidator().Validate(null, Bind(new() { [key] = value }));

        Assert.True(result.Failed);
    }

    private static ComplianceOptions Bind(Dictionary<string, string?> settings)
    {
        var options = new ComplianceOptions();
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build().Bind(options);
        return options;
    }
}

public sealed class WebhookSignatureTests
{
    private const string Secret = "test-webhook-secret";
    private const string Body = """{"id":"evt_1","amount":"100.00"}""";
    private static readonly DateTimeOffset Now = Fixtures.Start;
    private static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    [Fact]
    public void ValidSignature_IsAccepted()
    {
        Assert.True(WebhookSignature.Verify(Secret, WebhookSignature.Sign(Secret, Now, Body), Body, Now, Tolerance));
    }

    [Fact]
    public void TamperedBody_IsRejected()
    {
        var header = WebhookSignature.Sign(Secret, Now, Body);

        Assert.False(WebhookSignature.Verify(Secret, header, Body.Replace("100.00", "900.00", StringComparison.Ordinal), Now, Tolerance));
    }

    [Fact]
    public void WrongSecret_IsRejected()
    {
        Assert.False(WebhookSignature.Verify(Secret, WebhookSignature.Sign("another-secret", Now, Body), Body, Now, Tolerance));
    }

    [Theory]
    [InlineData(-6)]
    [InlineData(6)]
    public void TimestampOutsideTolerance_IsRejected(int minutes)
    {
        var header = WebhookSignature.Sign(Secret, Now.AddMinutes(minutes), Body);

        Assert.False(WebhookSignature.Verify(Secret, header, Body, Now, Tolerance));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("v1=abc")]
    [InlineData("t=1764000000")]
    [InlineData("t=abc,v1=00")]
    [InlineData("t=1764000000,v1=zz")]
    public void MalformedHeader_IsRejected(string? header)
    {
        Assert.False(WebhookSignature.Verify(Secret, header, Body, Now, Tolerance));
    }

    [Fact]
    public void RotatedSecret_AcceptsEitherSignature()
    {
        var current = WebhookSignature.Sign(Secret, Now, Body);
        var rotated = WebhookSignature.Sign("previous-secret", Now, Body);
        var header = $"{rotated},{current.Split(',')[1]}";

        Assert.True(WebhookSignature.Verify(Secret, header, Body, Now, Tolerance));
    }
}
