using FiatCryptoGateway.Application.Alerts;
using FiatCryptoGateway.Sandbox;
using Microsoft.Extensions.Options;

namespace FiatCryptoGateway.Tests;

public sealed class SystemStatusTests
{
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly AlertProblem Stale = new(AlertKeys.BackupStale, AlertSeverity.Urgent, "No database backup has succeeded for 27 hours.");
    private static readonly AlertProblem Wallet = new("wallet:c", AlertSeverity.Warning, "A new polygon wallet for acme has waited 20 hours for review.");

    private readonly ManualTimeProvider _time = new(Fixtures.Start);
    private readonly InMemoryBackupRunStore _backups = new();
    private readonly InMemoryAlertLog _log = new();
    private readonly FakeSource _source = new();
    private readonly AlertOptions _options = new() { BackupMaxAgeHours = 26 };

    [Fact]
    public async Task WithoutABackupContainer_BackupsAreNotSetUp()
    {
        _options.BackupMaxAgeHours = 0;

        var status = await GetAsync();

        Assert.Equal((BackupState.NotSetUp, false, null, null), (status.Backups.State, status.Backups.Watched, status.Backups.LatestSuccess, status.Backups.Latest));
        Assert.Empty(status.Backups.Recent);
    }

    [Fact]
    public async Task WatchedBackups_WaitForTheFirstRun_ThenReportStaleIfNoneComes()
    {
        Assert.Equal(BackupState.WaitingForFirst, (await GetAsync()).Backups.State);

        _source.Problems = [Stale];
        Assert.Equal(BackupState.Stale, (await GetAsync()).Backups.State);
    }

    [Fact]
    public async Task ASuccessfulRecentBackup_IsOk()
    {
        await _backups.RecordAsync(new BackupRun(Fixtures.Start.AddHours(-3), true, "gateway-20261124T090000Z.dump.age", null, 85_000), None);

        var status = await GetAsync();

        Assert.Equal(BackupState.Ok, status.Backups.State);
        Assert.Equal(26, status.Backups.MaxAgeHours);
        Assert.Equal("gateway-20261124T090000Z.dump.age", status.Backups.LatestSuccess!.File);
        Assert.Equal(status.Backups.LatestSuccess, status.Backups.Latest);
    }

    [Fact]
    public async Task AFailedLatestAttempt_IsFailed_AndStillShowsTheLastGoodBackup()
    {
        await _backups.RecordAsync(new BackupRun(Fixtures.Start.AddHours(-30), true, "gateway-20261123T090000Z.dump.age", null, 85_000), None);
        await _backups.RecordAsync(new BackupRun(Fixtures.Start.AddHours(-6), false, null, "Exit status 3."), None);
        _source.Problems = [Stale];

        var status = await GetAsync();

        Assert.Equal(BackupState.Failed, status.Backups.State); // failure outranks staleness: it says why
        Assert.Equal("Exit status 3.", status.Backups.Latest!.Detail);
        Assert.Equal("gateway-20261123T090000Z.dump.age", status.Backups.LatestSuccess!.File);
    }

    [Fact]
    public async Task ASuccessThatIsTooOld_IsStale()
    {
        await _backups.RecordAsync(new BackupRun(Fixtures.Start.AddHours(-27), true, "gateway-20261123T090000Z.dump.age", null, 85_000), None);
        _source.Problems = [Stale];

        Assert.Equal(BackupState.Stale, (await GetAsync()).Backups.State);
    }

    [Fact]
    public async Task RecentBackups_AreTheLastSevenNewestFirst()
    {
        for (var day = 0; day < 9; day++)
        {
            await _backups.RecordAsync(new BackupRun(Fixtures.Start.AddDays(-9 + day), true, $"gateway-{day}.dump.age", null, 1_000 + day), None);
        }

        var recent = (await GetAsync()).Backups.Recent;

        Assert.Equal(7, recent.Count);
        Assert.Equal(Fixtures.Start.AddDays(-1), recent[0].FinishedAt);
        Assert.Equal(Fixtures.Start.AddDays(-7), recent[^1].FinishedAt);
    }

    [Fact]
    public async Task OpenProblems_AreWhatTheAlertCheckSeesRightNow_EvenWithEmailOff()
    {
        _source.Problems = [Wallet, Stale];

        var status = await GetAsync();

        Assert.False(status.Alerts.EmailOn);
        Assert.Equal(0, status.Alerts.Recipients);
        Assert.Equal([Stale, Wallet], status.Alerts.Open); // urgent first
    }

    [Fact]
    public async Task OpenProblems_ListUrgentFirst_ThenInTheOrderFound()
    {
        var warningB = new AlertProblem("wallet:d", AlertSeverity.Warning, "Second warning.");
        var urgentB = new AlertProblem("held:x:overdue", AlertSeverity.Urgent, "Second urgent.");
        _source.Problems = [Wallet, warningB, urgentB, Stale];

        Assert.Equal([urgentB, Stale, Wallet, warningB], (await GetAsync()).Alerts.Open);
    }

    [Fact]
    public async Task AlertEmails_AreSummarisedByKind_NewestFirst_AtMostTen()
    {
        _options.To = "ops@example.com, compliance@example.com";
        _options.From = "gateway@example.com";
        for (var hour = 0; hour < 11; hour++)
        {
            await _log.AppendAsync(new AlertLogEntry(Fixtures.Start.AddHours(hour), AlertKind.Problems, ["held:a:due"]), None);
        }

        await _log.AppendAsync(
            new AlertLogEntry(Fixtures.Start.AddHours(11), AlertKind.Problems, ["held:a:overdue", "held:b:due", "wallet:c", "webhook:d", "webhook:e", "reconciliation:p:2026-11-26", "backup:stale", "mystery:x"]),
            None);
        await _log.AppendAsync(new AlertLogEntry(Fixtures.Start.AddHours(12), AlertKind.AllClear, []), None);

        var alerts = (await GetAsync()).Alerts;

        Assert.True(alerts.EmailOn);
        Assert.Equal(2, alerts.Recipients);
        Assert.Equal(10, alerts.RecentEmails.Count);
        Assert.Equal((Fixtures.Start.AddHours(12), AlertKind.AllClear, 0, "No problems"), Tuple(alerts.RecentEmails[0]));
        Assert.Equal(
            (Fixtures.Start.AddHours(11), AlertKind.Problems, 8,
             "2 held payments, 1 wallet waiting for review, 1 reconciliation day with differences, 2 failed notifications, 1 backup problem, 1 other problem"),
            Tuple(alerts.RecentEmails[1]));
        Assert.Equal((Fixtures.Start.AddHours(10), AlertKind.Problems, 1, "1 held payment"), Tuple(alerts.RecentEmails[2]));
    }

    [Fact]
    public void ProblemKeys_AreDescribedInWords()
    {
        Assert.Equal("No problems", AlertKeys.Describe([]));
        Assert.Equal("1 held payment", AlertKeys.Describe(["held:a:due"]));
        Assert.Equal("1 failed notification, 1 backup problem", AlertKeys.Describe(["backup:failed:20261126T090000", "webhook:x"]));
    }

    private static (DateTimeOffset, AlertKind, int, string) Tuple(AlertEmail email) => (email.SentAt, email.Kind, email.Problems, email.Summary);

    private Task<SystemStatus> GetAsync() =>
        new SystemStatusService(_source, _backups, _log, Options.Create(_options), _time).GetAsync(None);

    private sealed class FakeSource : IAlertSource
    {
        public IReadOnlyList<AlertProblem> Problems { get; set; } = [];

        public Task<IReadOnlyList<AlertProblem>> FindAsync(CancellationToken cancellationToken) => Task.FromResult(Problems);
    }
}
