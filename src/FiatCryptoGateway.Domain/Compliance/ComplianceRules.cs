using FiatCryptoGateway.Domain.Assets;

namespace FiatCryptoGateway.Domain.Compliance;

public enum KycLevel
{
    None,
    Basic,
    Full,
}

public enum ComplianceOutcome
{
    Approved,
    ManualReview,
    Rejected,
}

public enum WalletRisk
{
    Low,
    High,
    Blocked,
    Unknown,
}

/// <summary>What the KYC provider knows about a customer. The gateway stores references, not documents.</summary>
public sealed record CustomerProfile(
    string ClientId,
    string CustomerId,
    KycLevel KycLevel,
    string CountryCode,
    bool SanctionsMatch);

public sealed record WalletScreeningResult(WalletRisk Risk, string Screener, string? Reference);

/// <summary>A screening result bound to one order; an order refuses a decision made for another order.</summary>
public sealed record ComplianceDecision(
    Guid OrderId,
    ComplianceOutcome Outcome,
    IReadOnlyList<string> Reasons,
    string Screener,
    DateTimeOffset DecidedAt);

/// <summary>Per-order limits are in the pivot currency. Cumulative (daily/monthly) limits are not yet modelled.</summary>
public sealed record CompliancePolicy(
    IReadOnlySet<string> BlockedCountries,
    decimal BasicKycLimit,
    decimal FullKycLimit,
    decimal ManualReviewThreshold);

/// <summary>
/// Deterministic screening rules. Any rejection reason rejects; otherwise any review reason holds the
/// order for a person; only a clean result approves. Unknown wallet risk is never treated as low.
/// </summary>
public static class ComplianceRules
{
    public static (ComplianceOutcome Outcome, IReadOnlyList<string> Reasons) Evaluate(
        CustomerProfile customer,
        WalletScreeningResult wallet,
        Money pivotAmount,
        CompliancePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(customer);
        ArgumentNullException.ThrowIfNull(wallet);
        ArgumentNullException.ThrowIfNull(pivotAmount);
        ArgumentNullException.ThrowIfNull(policy);

        var rejections = new List<string>();
        var reviews = new List<string>();

        if (customer.SanctionsMatch)
        {
            rejections.Add("CUSTOMER_SANCTIONS_MATCH");
        }

        if (policy.BlockedCountries.Contains(customer.CountryCode))
        {
            rejections.Add("COUNTRY_NOT_SUPPORTED");
        }

        var limit = customer.KycLevel switch
        {
            KycLevel.Basic => policy.BasicKycLimit,
            KycLevel.Full => policy.FullKycLimit,
            _ => 0m,
        };

        if (customer.KycLevel == KycLevel.None)
        {
            rejections.Add("KYC_REQUIRED");
        }
        else if (pivotAmount.Amount > limit)
        {
            rejections.Add("KYC_LIMIT_EXCEEDED");
        }

        switch (wallet.Risk)
        {
            case WalletRisk.Blocked:
                rejections.Add("WALLET_BLOCKED");
                break;
            case WalletRisk.High:
                reviews.Add("WALLET_HIGH_RISK");
                break;
            case WalletRisk.Unknown:
                reviews.Add("WALLET_RISK_UNKNOWN");
                break;
        }

        if (pivotAmount.Amount >= policy.ManualReviewThreshold)
        {
            reviews.Add("AMOUNT_REQUIRES_REVIEW");
        }

        if (rejections.Count > 0)
        {
            return (ComplianceOutcome.Rejected, rejections.AsReadOnly());
        }

        return reviews.Count > 0
            ? (ComplianceOutcome.ManualReview, reviews.AsReadOnly())
            : (ComplianceOutcome.Approved, Array.Empty<string>());
    }
}
