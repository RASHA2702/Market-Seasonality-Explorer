using System.Globalization;
using System.Text;
using FiatCryptoGateway.Application.Compliance;
using FiatCryptoGateway.Application.Configuration;
using FiatCryptoGateway.Application.Payments;
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
/// What needs a person now: held payments and new wallets waiting long for review, payments stuck between the network
/// and the order, reconciliation differences, notifications the gateway gave up on in the last day, and failed or
/// missing backups. A stuck payment is urgent: a customer's money is involved and the merchant has not been paid. Texts
/// carry no customer details: short order IDs, merchants and counts only.
/// </summary>
public sealed class AlertChecks(
    ComplianceReviewService reviews,
    SettlementService settlement,
    IPaymentAttemptStore attempts,
    IReconciliationStore reconciliation,
    IOutbox outbox,
    IBackupRunStore backups,
    IOptions<AlertOptions> options,
    IOptions<ComplianceOptions> compliance,
    IOptions<ProcessorOptions> processor,
    TimeProvider time) : IAlertSource
{
    /// <summary>The most stuck payments of each kind named at once; a flood is worth one look, not hundreds of lines.</summary>
    private const int MaximumPerKind = 200;

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

        // The sweep captures approved payments and records captured ones within seconds, and gives an approval up after the
        // lapse time. Anything past these thresholds means the sweep cannot finish, or that a customer may be debited.
        var lapseHours = processor.Value.AuthorizationLapseHours;
        var stuckBefore = now - TimeSpan.FromMinutes(settings.StuckPaymentAfterMinutes);
        foreach (var uncaptured in await attempts.ListStuckAuthorizedAsync(stuckBefore, MaximumPerKind, cancellationToken))
        {
            problems.Add(new($"payment:uncaptured:{uncaptured.AttemptId:N}", AlertSeverity.Urgent,
                $"A payment for order {Short(uncaptured.OrderId)} ({uncaptured.ClientId}) was approved {Minutes(now - uncaptured.UpdatedAt)} minutes ago " +
                $"and could not be captured. The gateway keeps trying, and gives the approval up after {lapseHours} hours."));
        }

        // Oldest first, so the first one that is not old enough ends the list.
        foreach (var unrecorded in await attempts.ListCapturedNotRecordedAsync(MaximumPerKind, cancellationToken))
        {
            var taken = unrecorded.CapturedAt ?? unrecorded.UpdatedAt;
            if (taken > stuckBefore)
            {
                break;
            }

            problems.Add(new($"payment:unrecorded:{unrecorded.AttemptId:N}", AlertSeverity.Urgent,
                $"Money for order {Short(unrecorded.OrderId)} ({unrecorded.ClientId}) was taken {Minutes(now - taken)} minutes ago, " +
                "but the order has not recorded it. The gateway keeps trying."));
        }

        foreach (var lapsed in await attempts.ListLapsedAsync(now.AddDays(-settings.LapsedApprovalReportDays), MaximumPerKind, cancellationToken))
        {
            problems.Add(new($"payment:lapsed:{lapsed.AttemptId:N}", AlertSeverity.Urgent,
                $"An approval for order {Short(lapsed.OrderId)} ({lapsed.ClientId}) was given up after {lapseHours} hours without being captured. " +
                "Check the payment network's own report to be sure the customer was not debited."));
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
                problems.Add(new(AlertKeys.BackupStale, AlertSeverity.Urgent, success is null
                    ? $"No database backup has succeeded since the gateway started {Hours(now - since)} hours ago."
                    : $"No database backup has succeeded for {Hours(now - since)} hours."));
            }
        }

        return problems;
    }

    private static string Short(Guid id) => id.ToString("N", CultureInfo.InvariantCulture)[..8].ToUpperInvariant();

    private static string Hours(TimeSpan span) => ((int)Math.Floor(span.TotalHours)).ToString(CultureInfo.InvariantCulture);

    private static string Minutes(TimeSpan span) => ((int)Math.Floor(span.TotalMinutes)).ToString(CultureInfo.InvariantCulture);

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
                .AppendLine("no payment is stuck or was given up, no reconciliation differences are open, no notification has failed in the last day,")
                .AppendLine("and backups are succeeding.");
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
