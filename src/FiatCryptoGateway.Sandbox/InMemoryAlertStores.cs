using FiatCryptoGateway.Application.Alerts;

namespace FiatCryptoGateway.Sandbox;

public sealed class InMemoryBackupRunStore : IBackupRunStore
{
    private readonly Lock _gate = new();
    private readonly List<BackupRun> _runs = [];

    public Task<(BackupRun? Latest, BackupRun? LatestSuccess)> GetLatestAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult((_runs.LastOrDefault(), _runs.LastOrDefault(run => run.Succeeded)));
        }
    }

    public Task RecordAsync(BackupRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        lock (_gate)
        {
            _runs.Add(run);
            return Task.CompletedTask;
        }
    }
}

public sealed class InMemoryAlertLog : IAlertLog
{
    private readonly Lock _gate = new();
    private readonly List<AlertLogEntry> _entries = [];

    public Task<AlertLogEntry?> GetLastAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_entries.LastOrDefault());
        }
    }

    public Task AppendAsync(AlertLogEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            _entries.Add(entry);
            return Task.CompletedTask;
        }
    }
}
