using System.Globalization;
using System.Text;
using FiatCryptoGateway.Application.Compliance;
using FiatCryptoGateway.Application.Configuration;
using FiatCryptoGateway.Application.Ports;
using FiatCryptoGateway.Application.Reconciliation;
using FiatCryptoGateway.Application.Settlement;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FiatCryptoGateway.Application.Alerts;

public enum AlertSeverity
{
    Warning,
    Urgent,
}

/// <summary>
/// Something a person should look at. The key stays the same while the problem lasts and changes when it gets worse
/// (a held payment passing its limit), so only what is new triggers an email.
/// </summary>
public sealed record AlertProblem(string Key, AlertSeverity Severity, string Text);

public interface IAlertSource
{
    Task<IReadOnlyList<AlertProblem>> FindAsync(CancellationToken cancellationToken);
}

public sealed record AlertMessage(IReadOnlyList<string> To, string From, string Subject, string Body);

public interface IAlertSender
{
    Task SendAsync(AlertMessage message, CancellationToken cancellationToken);
}

/// <summary>
/// What needs a person now: held payments and new wallets waiting long for review, reconciliation differences,
/// notifications the gateway gave up on in the last day, and failed or missing backups. Texts carry no customer
/// details: short order IDs, merchants and counts only.
/// </summary>
public sealed class AlertChecks(
    ComplianceReviewService reviews,
    SettlementService settlement,
    IReconciliationStore reconciliation,
    IOutbox outbox,
    IBackupRunStore backups,
    IOptions<AlertOptions> options,
    IOptions<ComplianceOptions> compliance,
    TimeProvider time) : IAlertSource
{
    private readonly DateTimeOffset _startedAt = time.GetUtcNow();

    public async Task<IReadOnlyList<AlertProblem>> FindAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var limit = compliance.Value.ReviewSlaHours;
        var warnAfter = TimeSpan.FromHours(settings.ReviewWarnAfterHours);
        var now = time.GetUtcNow();
        var problems = new List<AlertProblem>();

        foreach (var held in await reviews.GetQueueAsync(cancellationToken))
        {
            var id = held.Order.OrderId;
            if (held.Overdue)
            {
                problems.Add(new($"held:{id:N}:overdue", AlertSeverity.Urgent,
                    $"Held payment {Short(id)} for {held.Order.ClientId} has waited {Hours(held.Waiting)} hours, past the {limit}-hour limit."));
            }
            else if (held.Waiting >= warnAfter)
            {
                problems.Add(new($"held:{id:N}:due", AlertSeverity.Warning,
                    $"Held payment {Short(id)} for {held.Order.ClientId} has waited {Hours(held.Waiting)} hours; the limit is {limit} hours."));
            }
        }

        foreach (var wallet in await settlement.ListForReviewAsync(cancellationToken))
        {
            if (now - wallet.RegisteredAt >= warnAfter)
            {
                problems.Add(new($"wallet:{wallet.WalletId:N}", AlertSeverity.Warning,
                    $"A new {wallet.Network} wallet for {wallet.ClientId} has waited {Hours(now - wallet.RegisteredAt)} hours for review."));
            }
        }

        var latestRuns = (await reconciliation.ListAsync(day: null, limit: 60, cancellationToken))
            .GroupBy(run => (run.Provider, run.Day))
            .Select(group => group.MaxBy(run => run.RanAt)!);
        foreach (var run in latestRuns.Where(run => !run.Clean).OrderBy(run => run.Day))
        {
            var unbooked = run.Discrepancies.Any(difference => difference.Type == DiscrepancyType.MissingInLedger);
            problems.Add(new(
                $"reconciliation:{run.Provider}:{run.Day:yyyy-MM-dd}",
                unbooked ? AlertSeverity.Urgent : AlertSeverity.Warning,
                $"Reconciliation of {run.Day:yyyy-MM-dd} ({run.Provider}) found {Count(run.Discrepancies.Count, "difference")}" +
                (unbooked ? ", including money the provider reports that is not in the books." : ".")));
        }

        foreach (var failed in await outbox.ListFailedAsync(now.AddDays(-1), 200, cancellationToken))
        {
            problems.Add(new($"webhook:{failed.MessageId:N}", AlertSeverity.Warning,
                $"A {failed.EventType} notification to {failed.ClientId} was given up after {Count(failed.Attempts, "attempt")} ({failed.LastError ?? "no answer"})."));
        }

        if (settings.BackupMaxAgeHours > 0)
        {
            var (latest, success) = await backups.GetLatestAsync(cancellationToken);
            if (latest is { Succeeded: false })
            {
                problems.Add(new($"backup:failed:{latest.FinishedAt.UtcDateTime:yyyyMMddTHHmmss}", AlertSeverity.Urgent,
                    $"The database backup at {latest.FinishedAt.UtcDateTime:yyyy-MM-dd HH:mm} UTC failed. {latest.Detail}".TrimEnd()));
            }

            var since = success?.FinishedAt ?? _startedAt;
            if (now - since > TimeSpan.FromHours(settings.BackupMaxAgeHours))
            {
                problems.Add(new("backup:stale", AlertSeverity.Urgent, success is null
                    ? $"No database backup has succeeded since the gateway started {Hours(now - since)} hours ago."
                    : $"No database backup has succeeded for {Hours(now - since)} hours."));
            }
        }

        return problems;
    }

    private static string Short(Guid id) => id.ToString("N", CultureInfo.InvariantCulture)[..8].ToUpperInvariant();

    private static string Hours(TimeSpan span) => ((int)Math.Floor(span.TotalHours)).ToString(CultureInfo.InvariantCulture);

    private static string Count(int count, string noun) =>
        count.ToString(CultureInfo.InvariantCulture) + " " + noun + (count == 1 ? string.Empty : "s");
}

public enum AlertOutcome
{
    Off,
    NothingNew,
    SentProblems,
    SentReminder,
    SentAllClear,
}

/// <summary>
/// Emails the people in <see cref="AlertOptions.To"/>: when a new problem appears, again after
/// <see cref="AlertOptions.RemindAfterHours"/> while problems stay open, and once when everything is clear. What was
/// emailed is logged, so a restart says nothing twice; a failed send is not logged and is tried on the next check.
/// One instance checks at a time.
/// </summary>
public sealed partial class AlertService(
    IAlertSource source,
    IAlertLog log,
    IAlertSender sender,
    IOrderLocks locks,
    IOptions<AlertOptions> options,
    IOptions<GatewaySafetyOptions> safety,
    TimeProvider time,
    ILogger<AlertService> logger)
{
    // The order locks double as the "one alert check at a time" lock; no order has this ID.
    private static readonly Guid CheckLock = new("a1e47000-0000-4000-8000-00000000a1e4");

    public async Task<AlertOutcome> RunAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!settings.IsOn)
        {
            return AlertOutcome.Off;
        }

        await using var turn = await locks.AcquireAsync(CheckLock, cancellationToken);
        var problems = await source.FindAsync(cancellationToken);
        var last = await log.GetLastAsync(cancellationToken);
        var now = time.GetUtcNow();
        var reported = last is { Kind: AlertKind.Problems } ? last.ProblemKeys.ToHashSet(StringComparer.Ordinal) : [];
        var fresh = problems.Where(problem => !reported.Contains(problem.Key)).ToList();

        AlertOutcome outcome;
        if (fresh.Count > 0)
        {
            outcome = AlertOutcome.SentProblems;
        }
        else if (problems.Count > 0 && now - last!.SentAt >= TimeSpan.FromHours(settings.RemindAfterHours))
        {
            outcome = AlertOutcome.SentReminder;
        }
        else if (problems.Count == 0 && last is { Kind: AlertKind.Problems })
        {
            outcome = AlertOutcome.SentAllClear;
        }
        else
        {
            return AlertOutcome.NothingNew;
        }

        await sender.SendAsync(Compose(outcome, problems, fresh, settings), cancellationToken);
        await log.AppendAsync(
            new AlertLogEntry(now, outcome == AlertOutcome.SentAllClear ? AlertKind.AllClear : AlertKind.Problems, [.. problems.Select(problem => problem.Key)]),
            cancellationToken);
        LogSent(logger, outcome, problems.Count);
        return outcome;
    }

    private AlertMessage Compose(AlertOutcome outcome, IReadOnlyList<AlertProblem> problems, List<AlertProblem> fresh, AlertOptions settings)
    {
        var tag = $"[Gateway {safety.Value.Mode}]";
        var body = new StringBuilder();
        string subject;
        if (outcome == AlertOutcome.SentAllClear)
        {
            subject = $"{tag} All clear";
            body.AppendLine("Everything the alert check watches is back to normal: no payment or wallet has waited too long for review,")
                .AppendLine("no reconciliation differences are open, no notification has failed in the last day, and backups are succeeding.");
        }
        else
        {
            var urgent = (outcome == AlertOutcome.SentProblems ? fresh : problems).Any(problem => problem.Severity == AlertSeverity.Urgent);
            subject = outcome == AlertOutcome.SentProblems
                ? $"{tag} {(urgent ? "URGENT: " : string.Empty)}{Count(fresh.Count, "new problem")} {(fresh.Count == 1 ? "needs" : "need")} a person"
                : $"{tag} {(urgent ? "URGENT: " : string.Empty)}Reminder: {Count(problems.Count, "problem")} still open";
            var newKeys = fresh.Select(problem => problem.Key).ToHashSet(StringComparer.Ordinal);
            AppendList(body, outcome == AlertOutcome.SentProblems ? "New since the last email:" : "Still open:", outcome == AlertOutcome.SentProblems ? fresh : problems);
            if (outcome == AlertOutcome.SentProblems && problems.Count > fresh.Count)
            {
                body.AppendLine();
                AppendList(body, "Still open from before:", [.. problems.Where(problem => !newKeys.Contains(problem.Key))]);
            }
        }

        body.AppendLine();
        if (settings.ConsoleUrl is { } console)
        {
            body.AppendLine(CultureInfo.InvariantCulture, $"Reviewer console: {console}").AppendLine();
        }

        var every = settings.CheckIntervalMinutes == 1 ? "minute" : Count(settings.CheckIntervalMinutes, "minute");
        body.AppendLine(CultureInfo.InvariantCulture, $"Sent by the gateway's alert check, every {every}. Open problems are emailed again after {Count(settings.RemindAfterHours, "hour")}, and you are told once when all are resolved. This email holds no customer details.");
        return new AlertMessage(settings.Recipients, settings.From, subject, body.ToString());
    }

    private static void AppendList(StringBuilder body, string heading, IEnumerable<AlertProblem> problems)
    {
        body.AppendLine(heading);
        foreach (var problem in problems)
        {
            body.AppendLine(CultureInfo.InvariantCulture, $"- {(problem.Severity == AlertSeverity.Urgent ? "URGENT: " : string.Empty)}{problem.Text}");
        }
    }

    private static string Count(int count, string noun) =>
        count.ToString(CultureInfo.InvariantCulture) + " " + noun + (count == 1 ? string.Empty : "s");

    [LoggerMessage(Level = LogLevel.Information, Message = "Alert email sent ({Outcome}); {Open} problems open.")]
    private static partial void LogSent(ILogger logger, AlertOutcome outcome, int open);
}
