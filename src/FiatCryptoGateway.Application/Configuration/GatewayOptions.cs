using FiatCryptoGateway.Domain;
using FiatCryptoGateway.Domain.Assets;
using FiatCryptoGateway.Domain.Compliance;
using FiatCryptoGateway.Domain.Pricing;
using Microsoft.Extensions.Options;

namespace FiatCryptoGateway.Application.Configuration;

public enum GatewayMode
{
    Sandbox,
    Production,
}

public sealed class GatewaySafetyOptions
{
    public const string SectionName = "Gateway";

    public GatewayMode Mode { get; set; } = GatewayMode.Sandbox;

    public bool AllowRealFundsMovement { get; set; }
}

/// <summary>
/// This build is sandbox-only. No licensed payment, liquidity or custody provider is integrated, so a
/// production setting is a startup error rather than a mode that could silently move real money.
/// </summary>
public sealed class GatewaySafetyGate : IValidateOptions<GatewaySafetyOptions>
{
    public ValidateOptionsResult Validate(string? name, GatewaySafetyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.Mode != GatewayMode.Sandbox)
        {
            return ValidateOptionsResult.Fail(
                "This build is sandbox-only: Gateway:Mode must be Sandbox. No licensed payment, liquidity or custody provider is integrated.");
        }

        if (options.AllowRealFundsMovement)
        {
            return ValidateOptionsResult.Fail("This build is sandbox-only: Gateway:AllowRealFundsMovement must remain false.");
        }

        return ValidateOptionsResult.Success;
    }
}

public sealed class PricingOptions
{
    public const string SectionName = "Gateway:Pricing";

    public string Version { get; set; } = "sandbox-1";

    public int ServiceFeeBasisPoints { get; set; } = 150;

    public int SpreadBasisPoints { get; set; } = 50;

    public int QuoteTimeToLiveSeconds { get; set; } = 60;

    public int PaymentWindowMinutes { get; set; } = 15;

    public int MaximumPriceAgeSeconds { get; set; } = 30;

    public string PivotCurrency { get; set; } = "USD";

    public decimal MinimumOrderAmount { get; set; } = 10m;

    public decimal MaximumOrderAmount { get; set; } = 10_000m;

    public PricingPolicy ToPolicy(AssetCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var policy = new PricingPolicy(
            Version,
            ServiceFeeBasisPoints,
            SpreadBasisPoints,
            TimeSpan.FromSeconds(QuoteTimeToLiveSeconds),
            TimeSpan.FromMinutes(PaymentWindowMinutes),
            TimeSpan.FromSeconds(MaximumPriceAgeSeconds),
            catalog.Get(AssetId.Fiat(PivotCurrency)),
            MinimumOrderAmount,
            MaximumOrderAmount);
        policy.Validate();
        return policy;
    }
}

public sealed class PricingOptionsValidator(AssetCatalog catalog) : IValidateOptions<PricingOptions>
{
    public ValidateOptionsResult Validate(string? name, PricingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        try
        {
            options.ToPolicy(catalog);
            return ValidateOptionsResult.Success;
        }
        catch (ArgumentException exception)
        {
            return ValidateOptionsResult.Fail($"Gateway:Pricing is invalid: {exception.Message}");
        }
        catch (GatewayRuleException exception)
        {
            return ValidateOptionsResult.Fail($"Gateway:Pricing is invalid: {exception.Message}");
        }
    }
}

public sealed class ComplianceOptions
{
    public const string SectionName = "Gateway:Compliance";

    /// <summary>
    /// ISO 3166-1 alpha-2 codes that cannot use the service. The defaults are a conservative placeholder;
    /// the real list is a legal decision. Configuration adds to this list and cannot remove from it.
    /// </summary>
    public IList<string> BlockedCountries { get; } = new List<string> { "CU", "IR", "KP", "SY" };

    /// <summary>Maximum order for customers with basic KYC, in the pivot currency.</summary>
    public decimal BasicKycLimit { get; set; } = 1_000m;

    /// <summary>Maximum order for fully verified customers, in the pivot currency.</summary>
    public decimal FullKycLimit { get; set; } = 10_000m;

    /// <summary>Orders at or above this amount, in the pivot currency, wait for a person to review them.</summary>
    public decimal ManualReviewThreshold { get; set; } = 5_000m;

    /// <summary>Held orders waiting longer than this are flagged as overdue in the review queue.</summary>
    public int ReviewSlaHours { get; set; } = 24;

    public CompliancePolicy ToPolicy() =>
        new(BlockedCountries.ToHashSet(StringComparer.Ordinal), BasicKycLimit, FullKycLimit, ManualReviewThreshold);
}
