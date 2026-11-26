using FiatCryptoGateway.Application.Alerts;
using Npgsql;

namespace FiatCryptoGateway.Persistence.Postgres;

public sealed class PostgresBackupRunStore(NpgsqlDataSource dataSource) : IBackupRunStore
{
    public async Task<(BackupRun? Latest, BackupRun? LatestSuccess)> GetLatestAsync(CancellationToken cancellationToken) =>
        (await ReadAsync("TRUE", cancellationToken), await ReadAsync("succeeded", cancellationToken));

    public async Task RecordAsync(BackupRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        await using var command = dataSource.CreateCommand(
            "INSERT INTO ops.backup_run (finished_at, succeeded, file, detail) VALUES (@at, @succeeded, @file, @detail)");
        await command
            .With("at", run.FinishedAt.Utc())
            .With("succeeded", run.Succeeded)
            .With("file", run.File)
            .With("detail", run.Detail)
            .ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<BackupRun?> ReadAsync(string condition, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            $"SELECT finished_at, succeeded, file, detail FROM ops.backup_run WHERE {condition} ORDER BY run_id DESC LIMIT 1");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new BackupRun(
            reader.GetFieldValue<DateTimeOffset>(0),
            reader.GetBoolean(1),
            await reader.IsDBNullAsync(2, cancellationToken) ? null : reader.GetString(2),
            await reader.IsDBNullAsync(3, cancellationToken) ? null : reader.GetString(3));
    }
}

public sealed class PostgresAlertLog(NpgsqlDataSource dataSource) : IAlertLog
{
    public async Task<AlertLogEntry?> GetLastAsync(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT sent_at, kind, problem_keys FROM ops.alert_log ORDER BY alert_id DESC LIMIT 1");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new AlertLogEntry(
                reader.GetFieldValue<DateTimeOffset>(0),
                DbNames.Parse<AlertKind>(reader.GetString(1)),
                reader.GetFieldValue<string[]>(2))
            : null;
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
