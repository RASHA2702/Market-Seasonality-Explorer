using FiatCryptoGateway.Domain;
using FiatCryptoGateway.Domain.Assets;
using FiatCryptoGateway.Domain.Compliance;
using FiatCryptoGateway.Domain.Pricing;
using System.Text.RegularExpressions;
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

public sealed partial class ComplianceOptions
{
    public const string SectionName = "Gateway:Compliance";

    /// <summary><c>AllowList</c> (default): only <see cref="Countries"/> may use the service. <c>BlockList</c>: everyone else may.</summary>
    public CountryPolicyMode CountryMode { get; set; } = CountryPolicyMode.AllowList;

    /// <summary>
    /// Comma-separated ISO 3166-1 alpha-2 codes, for example <c>IN</c> or <c>IN,AE</c>. Setting it replaces the
    /// default rather than adding to it. Defaults: India only for an allow-list (decision 7); the placeholder
    /// CU, IR, KP, SY for a block-list.
    /// </summary>
    public string? Countries { get; set; }

    /// <summary>Basic KYC limits, in the pivot currency: per payment, per rolling 24 hours, per rolling 30 days.</summary>
    public decimal BasicKycLimit { get; set; } = 1_000m;

    public decimal BasicKycDailyLimit { get; set; } = 2_000m;

    public decimal BasicKycMonthlyLimit { get; set; } = 5_000m;

    /// <summary>Full KYC limits, in the pivot currency: per payment, per rolling 24 hours, per rolling 30 days.</summary>
    public decimal FullKycLimit { get; set; } = 10_000m;

    public decimal FullKycDailyLimit { get; set; } = 20_000m;

    public decimal FullKycMonthlyLimit { get; set; } = 50_000m;

    /// <summary>Orders at or above this amount, in the pivot currency, wait for a person to review them.</summary>
    public decimal ManualReviewThreshold { get; set; } = 5_000m;

    /// <summary>Held orders waiting longer than this are flagged as overdue in the review queue.</summary>
    public int ReviewSlaHours { get; set; } = 24;

    public IReadOnlySet<string> EffectiveCountries =>
        string.IsNullOrWhiteSpace(Countries)
            ? CountryMode == CountryPolicyMode.AllowList
                ? new HashSet<string>(["IN"], StringComparer.Ordinal)
                : new HashSet<string>(["CU", "IR", "KP", "SY"], StringComparer.Ordinal)
            : Countries
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(code => code.ToUpperInvariant())
                .ToHashSet(StringComparer.Ordinal);

    public CompliancePolicy ToPolicy() =>
        new(
            CountryMode,
            EffectiveCountries,
            new KycLimits(BasicKycLimit, BasicKycDailyLimit, BasicKycMonthlyLimit),
            new KycLimits(FullKycLimit, FullKycDailyLimit, FullKycMonthlyLimit),
            ManualReviewThreshold);

    internal static bool IsCountryCode(string code) => CountryCode().IsMatch(code);

    [GeneratedRegex("^[A-Z]{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex CountryCode();
}

public sealed class ComplianceOptionsValidator : IValidateOptions<ComplianceOptions>
{
    public ValidateOptionsResult Validate(string? name, ComplianceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();

        var invalid = options.EffectiveCountries.Where(code => !ComplianceOptions.IsCountryCode(code)).ToList();
        if (invalid.Count > 0)
        {
            failures.Add($"Gateway:Compliance:Countries has invalid codes ({string.Join(", ", invalid)}); use ISO 3166-1 alpha-2, such as IN.");
        }

        foreach (var (level, perPayment, perDay, perMonth) in new[]
        {
            ("Basic", options.BasicKycLimit, options.BasicKycDailyLimit, options.BasicKycMonthlyLimit),
            ("Full", options.FullKycLimit, options.FullKycDailyLimit, options.FullKycMonthlyLimit),
        })
        {
            if (perPayment <= 0 || perDay < perPayment || perMonth < perDay)
            {
                failures.Add($"{level} KYC limits must be positive, with per payment <= per day <= per month.");
            }
        }

        if (options.ManualReviewThreshold <= 0 || options.ReviewSlaHours < 1)
        {
            failures.Add("ManualReviewThreshold must be positive and ReviewSlaHours at least 1.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
