using FiatCryptoGateway.Application.Alerts;
using Npgsql;

namespace FiatCryptoGateway.Persistence.Postgres;

public sealed class PostgresBackupRunStore(NpgsqlDataSource dataSource) : IBackupRunStore
{
    public async Task<(BackupRun? Latest, BackupRun? LatestSuccess)> GetLatestAsync(CancellationToken cancellationToken) =>
        (await ReadAsync("TRUE", cancellationToken), await ReadAsync("succeeded", cancellationToken));

    public async Task<IReadOnlyList<BackupRun>> ListRecentAsync(int limit, CancellationToken cancellationToken) =>
        await ReadManyAsync("TRUE", limit, cancellationToken);

    public async Task RecordAsync(BackupRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        await using var command = dataSource.CreateCommand(
            "INSERT INTO ops.backup_run (finished_at, succeeded, file, bytes, detail) VALUES (@at, @succeeded, @file, @bytes, @detail)");
        await command
            .With("at", run.FinishedAt.Utc())
            .With("succeeded", run.Succeeded)
            .With("file", run.File)
            .With("bytes", run.Bytes)
            .With("detail", run.Detail)
            .ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<BackupRun?> ReadAsync(string condition, CancellationToken cancellationToken)
    {
        var runs = await ReadManyAsync(condition, 1, cancellationToken);
        return runs.Count == 0 ? null : runs[0];
    }

    // The condition is one of this class's own constants, never input.
    private async Task<IReadOnlyList<BackupRun>> ReadManyAsync(string condition, int limit, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            $"SELECT finished_at, succeeded, file, detail, bytes FROM ops.backup_run WHERE {condition} ORDER BY run_id DESC LIMIT @limit");
        command.With("limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var runs = new List<BackupRun>();
        while (await reader.ReadAsync(cancellationToken))
        {
            runs.Add(new BackupRun(
                reader.GetFieldValue<DateTimeOffset>(0),
                reader.GetBoolean(1),
                await reader.IsDBNullAsync(2, cancellationToken) ? null : reader.GetString(2),
                await reader.IsDBNullAsync(3, cancellationToken) ? null : reader.GetString(3),
                await reader.IsDBNullAsync(4, cancellationToken) ? null : reader.GetInt64(4)));
        }

        return runs;
    }
}

public sealed class PostgresAlertLog(NpgsqlDataSource dataSource) : IAlertLog
{
    public async Task<AlertLogEntry?> GetLastAsync(CancellationToken cancellationToken)
    {
        var entries = await ListRecentAsync(1, cancellationToken);
        return entries.Count == 0 ? null : entries[0];
    }

    public async Task<IReadOnlyList<AlertLogEntry>> ListRecentAsync(int limit, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT sent_at, kind, problem_keys FROM ops.alert_log ORDER BY alert_id DESC LIMIT @limit");
        command.With("limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var entries = new List<AlertLogEntry>();
        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add(new AlertLogEntry(
                reader.GetFieldValue<DateTimeOffset>(0),
                DbNames.Parse<AlertKind>(reader.GetString(1)),
                reader.GetFieldValue<string[]>(2)));
        }

        return entries;
    }

    public async Task AppendAsync(AlertLogEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await using var command = dataSource.CreateCommand(
            "INSERT INTO ops.alert_log (sent_at, kind, problem_keys) VALUES (@at, @kind, @keys)");
        await command
            .With("at", entry.SentAt.Utc())
            .With("kind", DbNames.Of(entry.Kind))
            .With("keys", entry.ProblemKeys.ToArray())
            .ExecuteNonQueryAsync(cancellationToken);
    }
}
