using FiatCryptoGateway.Application.Alerts;
using Microsoft.Extensions.DependencyInjection;

namespace FiatCryptoGateway.Persistence.Tests;

[Collection(SharedPostgres.Name)]
public sealed class PostgresAlertStoreTests(PostgresFixture fixture)
{
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly DateTimeOffset Start = new(2028, 4, 1, 21, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task BackupRuns_GiveTheLatestAttemptAndTheLatestSuccess()
    {
        await using var services = fixture.BuildServices(TimeProvider.System);
        var store = services.GetRequiredService<IBackupRunStore>();

        Assert.Equal((null, null), await store.GetLatestAsync(None));

        await store.RecordAsync(new BackupRun(Start, true, "gateway-20280401T210000Z.dump.age", null), None);
        await store.RecordAsync(new BackupRun(Start.AddDays(1), false, null, "Exit status 3."), None);

        var (latest, success) = await store.GetLatestAsync(None);
        Assert.Equal(new BackupRun(Start.AddDays(1), false, null, "Exit status 3."), latest);
        Assert.Equal(new BackupRun(Start, true, "gateway-20280401T210000Z.dump.age", null), success);
    }

    [Fact]
    public async Task AlertLog_GivesTheLastEmailSent()
    {
        await using var services = fixture.BuildServices(TimeProvider.System);
        var log = services.GetRequiredService<IAlertLog>();

        Assert.Null(await log.GetLastAsync(None));

        await log.AppendAsync(new AlertLogEntry(Start, AlertKind.Problems, ["held:a:due", "webhook:b"]), None);
        await log.AppendAsync(new AlertLogEntry(Start.AddHours(1), AlertKind.AllClear, []), None);

        var last = await log.GetLastAsync(None);
        Assert.Equal((Start.AddHours(1), AlertKind.AllClear), (last!.SentAt, last.Kind));
        Assert.Empty(last.ProblemKeys);
        await log.AppendAsync(new AlertLogEntry(Start.AddHours(2), AlertKind.Problems, ["reconciliation:sandbox-fiat:2028-03-31"]), None);
        Assert.Equal(["reconciliation:sandbox-fiat:2028-03-31"], (await log.GetLastAsync(None))!.ProblemKeys);
    }
}
