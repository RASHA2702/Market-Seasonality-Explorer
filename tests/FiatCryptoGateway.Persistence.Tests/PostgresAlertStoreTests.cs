using FiatCryptoGateway.Application.Alerts;
using Microsoft.Extensions.DependencyInjection;

namespace FiatCryptoGateway.Persistence.Tests;

/// <summary>
/// The backup and alert logs are append-only and shared by every test in the database, so each test here records its
/// own rows and checks only the newest ones, never an empty start.
/// </summary>
[Collection(SharedPostgres.Name)]
public sealed class PostgresAlertStoreTests(PostgresFixture fixture)
{
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly DateTimeOffset Start = new(2028, 4, 1, 21, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task BackupRuns_GiveTheLatestAttempt_TheLatestSuccess_AndTheNewestRuns()
    {
        await using var services = fixture.BuildServices(TimeProvider.System);
        var store = services.GetRequiredService<IBackupRunStore>();

        await store.RecordAsync(new BackupRun(Start, true, "gateway-20280401T210000Z.dump.age", null, 85_000), None);
        await store.RecordAsync(new BackupRun(Start.AddDays(1), true, "gateway-20280402T210000Z.dump.age", null, 1_048_576), None);
        await store.RecordAsync(new BackupRun(Start.AddDays(2), false, null, "Exit status 3."), None);

        var (latest, success) = await store.GetLatestAsync(None);
        Assert.Equal(new BackupRun(Start.AddDays(2), false, null, "Exit status 3."), latest);
        Assert.Equal(new BackupRun(Start.AddDays(1), true, "gateway-20280402T210000Z.dump.age", null, 1_048_576), success);

        var recent = await store.ListRecentAsync(3, None);
        Assert.Equal([Start.AddDays(2), Start.AddDays(1), Start], recent.Select(run => run.FinishedAt));
        Assert.Equal([false, true, true], recent.Select(run => run.Succeeded));
        Assert.Equal(new long?[] { null, 1_048_576, 85_000 }, recent.Select(run => run.Bytes));
        Assert.Single(await store.ListRecentAsync(1, None));
    }

    [Fact]
    public async Task AlertLog_GivesTheLastEmailAndTheNewestEmails()
    {
        await using var services = fixture.BuildServices(TimeProvider.System);
        var log = services.GetRequiredService<IAlertLog>();

        await log.AppendAsync(new AlertLogEntry(Start, AlertKind.Problems, ["held:a:due", "webhook:b"]), None);
        await log.AppendAsync(new AlertLogEntry(Start.AddHours(1), AlertKind.AllClear, []), None);

        var last = await log.GetLastAsync(None);
        Assert.Equal((Start.AddHours(1), AlertKind.AllClear), (last!.SentAt, last.Kind));
        Assert.Empty(last.ProblemKeys);

        await log.AppendAsync(new AlertLogEntry(Start.AddHours(2), AlertKind.Problems, ["reconciliation:sandbox-fiat:2028-03-31"]), None);
        Assert.Equal(["reconciliation:sandbox-fiat:2028-03-31"], (await log.GetLastAsync(None))!.ProblemKeys);

        var recent = await log.ListRecentAsync(3, None);
        Assert.Equal([Start.AddHours(2), Start.AddHours(1), Start], recent.Select(entry => entry.SentAt));
        Assert.Equal([AlertKind.Problems, AlertKind.AllClear, AlertKind.Problems], recent.Select(entry => entry.Kind));
        Assert.Equal(["held:a:due", "webhook:b"], recent[2].ProblemKeys);
        Assert.Single(await log.ListRecentAsync(1, None));
    }
}
