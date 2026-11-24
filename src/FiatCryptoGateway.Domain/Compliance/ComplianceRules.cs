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

public enum CountryPolicyMode
{
    /// <summary>Only the listed countries may use the service.</summary>
    AllowList,

    /// <summary>Every country except the listed ones may use the service.</summary>
    BlockList,
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

/// <summary>
/// A customer's earlier payments, in the pivot currency: the last 24 hours and the last 30 days.
/// Expired and refunded orders are not counted.
/// </summary>
public sealed record CustomerVolume(decimal LastDay, decimal LastMonth)
{
    public static CustomerVolume None { get; } = new(0m, 0m);
}

/// <summary>Limits per KYC level, in the pivot currency: per payment, per rolling day and per rolling month.</summary>
public sealed record KycLimits(decimal PerPayment, decimal PerDay, decimal PerMonth);

public sealed record CompliancePolicy(
    CountryPolicyMode CountryMode,
    IReadOnlySet<string> Countries,
    KycLimits Basic,
    KycLimits Full,
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
        CustomerVolume volume,
        CompliancePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(customer);
        ArgumentNullException.ThrowIfNull(wallet);
        ArgumentNullException.ThrowIfNull(pivotAmount);
        ArgumentNullException.ThrowIfNull(volume);
        ArgumentNullException.ThrowIfNull(policy);

        var rejections = new List<string>();
        var reviews = new List<string>();

        if (customer.SanctionsMatch)
        {
            rejections.Add("CUSTOMER_SANCTIONS_MATCH");
        }

        var listed = policy.Countries.Contains(customer.CountryCode);
        if (policy.CountryMode == CountryPolicyMode.AllowList ? !listed : listed)
        {
            rejections.Add("COUNTRY_NOT_SUPPORTED");
        }

        var limits = customer.KycLevel switch
        {
            KycLevel.Basic => policy.Basic,
            KycLevel.Full => policy.Full,
            _ => null,
        };

        if (limits is null)
        {
            rejections.Add("KYC_REQUIRED");
        }
        else
        {
            if (pivotAmount.Amount > limits.PerPayment)
            {
                rejections.Add("KYC_LIMIT_EXCEEDED");
            }

            if (volume.LastDay + pivotAmount.Amount > limits.PerDay)
            {
                rejections.Add("DAILY_LIMIT_EXCEEDED");
            }

            if (volume.LastMonth + pivotAmount.Amount > limits.PerMonth)
            {
                rejections.Add("MONTHLY_LIMIT_EXCEEDED");
            }
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
