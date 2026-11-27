namespace FiatCryptoGateway.Application.Alerts;

/// <summary>One attempt of the backup container (deploy/backup), as it recorded it in the database.</summary>
public sealed record BackupRun(DateTimeOffset FinishedAt, bool Succeeded, string? File, string? Detail, long? Bytes = null);

/// <summary>The backup container's log of attempts. Append-only.</summary>
public interface IBackupRunStore
{
    /// <summary>The latest attempt, and the latest successful one.</summary>
    Task<(BackupRun? Latest, BackupRun? LatestSuccess)> GetLatestAsync(CancellationToken cancellationToken);

    /// <summary>The latest <paramref name="limit"/> attempts, newest first.</summary>
    Task<IReadOnlyList<BackupRun>> ListRecentAsync(int limit, CancellationToken cancellationToken);

    /// <summary>Records an attempt. The backup container writes its own rows; this is for other recorders and tests.</summary>
    Task RecordAsync(BackupRun run, CancellationToken cancellationToken);
}

public enum AlertKind
{
    Problems,
    AllClear,
}

/// <summary>An alert email that was sent: when, whether it reported problems or all clear, and which problems.</summary>
public sealed record AlertLogEntry(DateTimeOffset SentAt, AlertKind Kind, IReadOnlyList<string> ProblemKeys);

/// <summary>The alert emails sent so far. Append-only.</summary>
public interface IAlertLog
{
    Task<AlertLogEntry?> GetLastAsync(CancellationToken cancellationToken);

    /// <summary>The latest <paramref name="limit"/> emails, newest first.</summary>
    Task<IReadOnlyList<AlertLogEntry>> ListRecentAsync(int limit, CancellationToken cancellationToken);

    Task AppendAsync(AlertLogEntry entry, CancellationToken cancellationToken);
}
