using System.Globalization;
using FiatCryptoGateway.Application.OnRamp;
using FiatCryptoGateway.Application.Ports;
using FiatCryptoGateway.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FiatCryptoGateway.Application.Reconciliation;

public sealed class ReconciliationOptions
{
    public const string SectionName = "Gateway:Reconciliation";

    public bool Enabled { get; set; } = true;

    /// <summary>The IANA time zone whose calendar days reports cover. India Standard Time by default.</summary>
    public string TimeZone { get; set; } = "Asia/Kolkata";

    /// <summary>The local hour after which the previous day is reconciled, when providers' reports are ready.</summary>
    public int RunAfterHour { get; set; } = 6;

    /// <summary>
    /// How many past days the daily run checks: 2 checks yesterday and re-checks the day before, so a movement a
    /// provider reports a day late stops showing as a discrepancy.
    /// </summary>
    public int LookbackDays { get; set; } = 2;

    public int CheckIntervalMinutes { get; set; } = 15;

    public TimeZoneInfo Zone => TimeZoneInfo.FindSystemTimeZoneById(TimeZone);
}

public sealed class ReconciliationOptionsValidator : IValidateOptions<ReconciliationOptions>
{
    public ValidateOptionsResult Validate(string? name, ReconciliationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        try
        {
            _ = options.Zone;
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            failures.Add($"Reconciliation TimeZone '{options.TimeZone}' is not a known time zone; use an IANA name such as Asia/Kolkata.");
        }

        if (options.RunAfterHour is < 0 or > 23)
        {
            failures.Add("Reconciliation RunAfterHour must be between 0 and 23.");
        }

        if (options.LookbackDays is < 1 or > 31)
        {
            failures.Add("Reconciliation LookbackDays must be between 1 and 31.");
        }

        if (options.CheckIntervalMinutes is < 1 or > 1_440)
        {
            failures.Add("Reconciliation CheckIntervalMinutes must be between 1 and 1440.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

/// <summary>
/// Compares each provider's daily settlement report with the books and records the result, including every
/// discrepancy. A run never changes the books: discrepancies are for a person to investigate.
/// </summary>
public sealed partial class ReconciliationService(
    IEnumerable<ISettlementReportSource> sources,
    ILedgerStore ledger,
    IReconciliationStore store,
    IOrderLocks locks,
    IOptions<ReconciliationOptions> options,
    TimeProvider time,
    ILogger<ReconciliationService> logger)
{
    /// <summary>Today's date in the reconciliation time zone.</summary>
    public DateOnly Today => BusinessDay.Of(time.GetUtcNow(), options.Value.Zone);

    /// <summary>Reconciles a finished day for every provider that publishes reports, and records each run.</summary>
    public async Task<IReadOnlyList<ReconciliationRun>> RunAsync(DateOnly day, CancellationToken cancellationToken)
    {
        if (day >= Today)
        {
            throw new GatewayRuleException(
                "DAY_NOT_FINISHED", $"{day:yyyy-MM-dd} has not ended in {options.Value.TimeZone}; only finished days can be reconciled.");
        }

        var runs = new List<ReconciliationRun>();
        foreach (var source in sources)
        {
            runs.Add((await RunOneAsync(source, day, skipIfAlreadyRunToday: false, cancellationToken))!);
        }

        return runs.AsReadOnly();
    }

    /// <summary>
    /// For the daily schedule: after <see cref="ReconciliationOptions.RunAfterHour"/>, reconciles each of the last
    /// <see cref="ReconciliationOptions.LookbackDays"/> days for every provider not yet reconciled for that day today.
    /// Safe to call often and from several instances. Returns the number of runs made.
    /// </summary>
    public async Task<int> RunDueAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var localNow = TimeZoneInfo.ConvertTime(time.GetUtcNow(), settings.Zone);
        if (localNow.Hour < settings.RunAfterHour)
        {
            return 0;
        }

        var today = DateOnly.FromDateTime(localNow.DateTime);
        var made = 0;
        for (var daysBack = 1; daysBack <= settings.LookbackDays; daysBack++)
        {
            foreach (var source in sources)
            {
                if (await RunOneAsync(source, today.AddDays(-daysBack), skipIfAlreadyRunToday: true, cancellationToken) is not null)
                {
                    made++;
                }
            }
        }

        return made;
    }

    private async Task<ReconciliationRun?> RunOneAsync(
        ISettlementReportSource source,
        DateOnly day,
        bool skipIfAlreadyRunToday,
        CancellationToken cancellationToken)
    {
        var zone = options.Value.Zone;
        await using (await locks.AcquireAsync(OrderLockKeys.Reconciliation(source.Provider, day), cancellationToken))
        {
            if (skipIfAlreadyRunToday)
            {
                var today = Today;
                var earlier = await store.ListAsync(day, limit: 100, cancellationToken);
                if (earlier.Any(run => run.Provider == source.Provider && BusinessDay.Of(run.RanAt, zone) == today))
                {
                    return null;
                }
            }

            var (start, end) = BusinessDay.Window(day, zone);
            var report = await source.GetReportAsync(day, start, end, cancellationToken);
            if (report.Provider != source.Provider || report.Day != day)
            {
                throw new InvalidOperationException($"The {source.Provider} report source returned a report for {report.Provider} on {report.Day}.");
            }

            var references = report.Lines.Select(line => line.Reference).Distinct(StringComparer.Ordinal).ToList();
            var entries = await ledger.ListForReconciliationAsync(source.Provider, start, end, references, cancellationToken);
            var result = Reconciler.Compare(report, entries, start, end);
            var run = new ReconciliationRun(
                Guid.NewGuid(), source.Provider, day, start, end, time.GetUtcNow(), report.Lines.Count, result.LedgerEntries, result.Matched, result.Discrepancies);
            await store.SaveAsync(run, cancellationToken);
            var dayText = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); // unambiguous in logs
            if (run.Clean)
            {
                LogClean(logger, source.Provider, dayText, run.Matched);
            }
            else
            {
                LogDiscrepancies(logger, source.Provider, dayText, run.Discrepancies.Count);
            }

            return run;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Reconciled {Provider} for {Day}: {Matched} movements matched, no discrepancies.")]
    private static partial void LogClean(ILogger logger, string provider, string day, int matched);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reconciliation of {Provider} for {Day} found {Count} discrepancies; review them in the reconciliation API.")]
    private static partial void LogDiscrepancies(ILogger logger, string provider, string day, int count);
}
