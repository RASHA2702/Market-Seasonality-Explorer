using System.Globalization;
using Microsoft.Extensions.Options;

namespace FiatCryptoGateway.Application.Alerts;

/// <summary>Problem keys: what kind of problem they name, in words.</summary>
public static class AlertKeys
{
    /// <summary>No backup has succeeded within the allowed time (or since the gateway started, if none has yet).</summary>
    public const string BackupStale = "backup:stale";

    private static readonly (string Prefix, string One, string Many)[] Categories =
    [
        ("held", "held payment", "held payments"),
        ("wallet", "wallet waiting for review", "wallets waiting for review"),
        ("reconciliation", "reconciliation day with differences", "reconciliation days with differences"),
        ("webhook", "failed notification", "failed notifications"),
        ("backup", "backup problem", "backup problems"),
    ];

    /// <summary>For example "2 held payments, 1 failed notification". Keys carry no detail beyond their kind here.</summary>
    public static string Describe(IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var counts = keys
            .GroupBy(key => key.Split(':', 2)[0], StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var parts = new List<string>();
        foreach (var (prefix, one, many) in Categories)
        {
            if (counts.Remove(prefix, out var count))
            {
                parts.Add(Counted(count, one, many));
            }
        }

        var other = counts.Values.Sum();
        if (other > 0)
        {
            parts.Add(Counted(other, "other problem", "other problems"));
        }

        return parts.Count == 0 ? "No problems" : string.Join(", ", parts);
    }

    private static string Counted(int count, string one, string many) =>
        count.ToString(CultureInfo.InvariantCulture) + " " + (count == 1 ? one : many);
}

public enum BackupState
{
    /// <summary>Nothing is recorded and none is expected: there is no backup container (local development).</summary>
    NotSetUp,

    /// <summary>Backups are watched, but none has finished yet.</summary>
    WaitingForFirst,

    Ok,

    /// <summary>The latest attempt failed.</summary>
    Failed,

    /// <summary>No backup has succeeded within the allowed time.</summary>
    Stale,
}

public sealed record BackupStatus(
    BackupState State,
    bool Watched,
    int MaxAgeHours,
    BackupRun? LatestSuccess,
    BackupRun? Latest,
    IReadOnlyList<BackupRun> Recent);

/// <summary>An alert email that was sent, with its problems summarised by kind.</summary>
public sealed record AlertEmail(DateTimeOffset SentAt, AlertKind Kind, int Problems, string Summary);

public sealed record AlertStatus(
    bool EmailOn,
    int Recipients,
    int CheckIntervalMinutes,
    int ReviewWarnAfterHours,
    IReadOnlyList<AlertProblem> Open,
    IReadOnlyList<AlertEmail> RecentEmails);

public sealed record SystemStatus(DateTimeOffset CheckedAt, BackupStatus Backups, AlertStatus Alerts);

/// <summary>
/// What the gateway reports about itself for the reviewer console: the state of the database backups, the problems the
/// alert check would email about right now (checked live, even while emails are off), and the emails sent lately.
/// </summary>
public sealed class SystemStatusService(
    IAlertSource source,
    IBackupRunStore backups,
    IAlertLog log,
    IOptions<AlertOptions> options,
    TimeProvider time)
{
    public const int RecentBackupRuns = 7;
    public const int RecentAlertEmails = 10;

    public async Task<SystemStatus> GetAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        // Urgent first; otherwise the order the checks found them in (the sort is stable).
        var open = (await source.FindAsync(cancellationToken)).OrderBy(problem => problem.Severity == AlertSeverity.Urgent ? 0 : 1).ToList();
        var (latest, success) = await backups.GetLatestAsync(cancellationToken);
        var recentRuns = await backups.ListRecentAsync(RecentBackupRuns, cancellationToken);
        var emails = await log.ListRecentAsync(RecentAlertEmails, cancellationToken);

        var watched = settings.BackupMaxAgeHours > 0;
        var stale = open.Any(problem => problem.Key == AlertKeys.BackupStale);
        var state = latest switch
        {
            null => stale ? BackupState.Stale : watched ? BackupState.WaitingForFirst : BackupState.NotSetUp,
            { Succeeded: false } => BackupState.Failed,
            _ => stale ? BackupState.Stale : BackupState.Ok,
        };

        return new SystemStatus(
            time.GetUtcNow(),
            new BackupStatus(state, watched, settings.BackupMaxAgeHours, success, latest, recentRuns),
            new AlertStatus(
                settings.IsOn,
                settings.Recipients.Count,
                settings.CheckIntervalMinutes,
                settings.ReviewWarnAfterHours,
                open,
                [.. emails.Select(entry => new AlertEmail(entry.SentAt, entry.Kind, entry.ProblemKeys.Count, AlertKeys.Describe(entry.ProblemKeys)))]));
    }
}
