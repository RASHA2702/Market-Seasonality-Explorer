using FiatCryptoGateway.Application.Webhooks;
using FiatCryptoGateway.Domain.Assets;
using FiatCryptoGateway.Domain.Compliance;

namespace FiatCryptoGateway.Tests;

public sealed class ComplianceRulesTests
{
    private static readonly CompliancePolicy Policy = new(
        new HashSet<string>(["KP", "IR"], StringComparer.Ordinal), BasicKycLimit: 1_000m, FullKycLimit: 10_000m, ManualReviewThreshold: 5_000m);

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
        int pivotAmount = 100) =>
        ComplianceRules.Evaluate(customer, new WalletScreeningResult(risk, "test", null), new Money(pivotAmount, Fixtures.Usd), Policy);
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
