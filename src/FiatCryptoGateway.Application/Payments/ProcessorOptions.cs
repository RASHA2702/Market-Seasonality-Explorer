using FiatCryptoGateway.Domain.Payments;
using Microsoft.Extensions.Options;

namespace FiatCryptoGateway.Application.Payments;

/// <summary>Who collects the customer's rupees.</summary>
public enum FiatProviderChoice
{
    /// <summary>The gateway's own payment processor, with its simulated card and UPI networks.</summary>
    Processor,

    /// <summary>The earlier simulated payment partner, which reports payments by webhook. Kept for its tests and for later.</summary>
    SandboxPartner,
}

public sealed class FiatProviderOptions
{
    public const string SectionName = "Gateway:Fiat";

    public FiatProviderChoice Provider { get; set; } = FiatProviderChoice.Processor;
}

public sealed class FiatProviderOptionsValidator : IValidateOptions<FiatProviderOptions>
{
    public ValidateOptionsResult Validate(string? name, FiatProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Enum.IsDefined(options.Provider)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("Fiat Provider must be Processor or SandboxPartner.");
    }
}

/// <summary>Settings of the gateway's own payment processor (<c>Gateway:Processor</c>).</summary>
public sealed class ProcessorOptions
{
    public const string SectionName = "Gateway:Processor";

    /// <summary>
    /// Minutes a customer has to finish one payment attempt (type their details, answer their bank's check). An attempt
    /// never outlives its order's own payment deadline.
    /// </summary>
    public int AttemptWindowMinutes { get; set; } = 10;

    /// <summary>Tries a customer gets to answer their bank's check before the attempt fails.</summary>
    public int AuthenticationTries { get; set; } = 3;

    /// <summary>Attempts one order may have, however they end. Slows down anyone trying many cards on one order.</summary>
    public int MaxAttemptsPerOrder { get; set; } = 10;

    /// <summary>Seconds an approved payment may wait to be captured before the background sweep finishes the job.</summary>
    public int StuckAfterSeconds { get; set; } = 120;

    /// <summary>
    /// Hours after which an approved payment that still cannot be captured is given up and flagged, rather than retried
    /// for ever. A real network drops an unused approval after about this long.
    /// </summary>
    public int AuthorizationLapseHours { get; set; } = 24;

    /// <summary>The most items each background sweep handles, so one pass stays short.</summary>
    public int SweepBatchSize { get; set; } = 100;
}

public sealed class ProcessorOptionsValidator : IValidateOptions<ProcessorOptions>
{
    public ValidateOptionsResult Validate(string? name, ProcessorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        Check(options.AttemptWindowMinutes, 1, 60, nameof(options.AttemptWindowMinutes), failures);
        Check(options.AuthenticationTries, 1, 10, nameof(options.AuthenticationTries), failures);
        Check(options.MaxAttemptsPerOrder, 1, 100, nameof(options.MaxAttemptsPerOrder), failures);
        Check(options.StuckAfterSeconds, 5, 3_600, nameof(options.StuckAfterSeconds), failures);
        Check(options.AuthorizationLapseHours, 1, 168, nameof(options.AuthorizationLapseHours), failures);
        Check(options.SweepBatchSize, 1, 1_000, nameof(options.SweepBatchSize), failures);
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void Check(int value, int minimum, int maximum, string setting, List<string> failures)
    {
        if (value < minimum || value > maximum)
        {
            failures.Add($"Processor {setting} must be between {minimum} and {maximum}.");
        }
    }
}

/// <summary>
/// The sandbox-only guard for payment rails, next to the guard that refuses any gateway mode but Sandbox. The API
/// refuses to start if any registered rail is not a simulated one, if two rails serve the same payment method, or if
/// there are none (the processor could not take a payment).
/// </summary>
public sealed class PaymentRailGuard(IEnumerable<IPaymentRail> rails) : IValidateOptions<ProcessorOptions>
{
    public ValidateOptionsResult Validate(string? name, ProcessorOptions options)
    {
        var all = rails.ToList();
        var failures = new List<string>();
        if (all.Count == 0)
        {
            failures.Add("No payment rail is registered, so no payment could be taken.");
        }

        failures.AddRange(
            all.Where(rail => !rail.IsSandbox)
                .Select(rail => $"Payment rail '{rail.Name}' is not a sandbox rail. This build is sandbox only and moves no real money."));
        failures.AddRange(
            all.GroupBy(rail => rail.Method)
                .Where(group => group.Count() > 1)
                .Select(group => $"More than one payment rail is registered for {group.Key}."));
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

/// <summary>
/// The registered payment rails, one per payment method. Two rails for one method are refused at startup by
/// <see cref="PaymentRailGuard"/>; this class only has to survive being built before that check runs.
/// </summary>
public sealed class PaymentRailSet
{
    private readonly Dictionary<PaymentMethod, IPaymentRail> _rails;

    public PaymentRailSet(IEnumerable<IPaymentRail> rails)
    {
        ArgumentNullException.ThrowIfNull(rails);
        _rails = rails.GroupBy(rail => rail.Method).ToDictionary(group => group.Key, group => group.First());
    }

    public IReadOnlyCollection<IPaymentRail> All => _rails.Values;

    public IPaymentRail? For(PaymentMethod method) => _rails.GetValueOrDefault(method);

    public IPaymentRail Require(PaymentMethod method) =>
        For(method) ?? throw new Domain.GatewayRuleException("PAYMENT_METHOD_UNAVAILABLE", "That way of paying is not available.");
}

/// <summary>
/// The identifiers the processor gives its own payments and refunds. They come from the attempt's identifier, so a retry
/// of the same attempt makes the same identifier and two attempts can never share one.
/// </summary>
public static class PaymentIdentifiers
{
    public static string Payment(Guid attemptId) => $"gp_pay_{attemptId:N}";

    public static string Refund(Guid attemptId) => $"gp_rfd_{attemptId:N}";
}
