using System.Security.Cryptography;
using FiatCryptoGateway.Application.Reviewers;
using Microsoft.Extensions.DependencyInjection;

namespace FiatCryptoGateway.Persistence.Tests;

[Collection(SharedPostgres.Name)]
public sealed class PostgresReviewerAccessTests(PostgresFixture fixture)
{
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly DateTimeOffset Start = new(2028, 3, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TheSameCode_OpensOneSession_EvenFromTwoInstancesAtOnce()
    {
        await using var instanceA = fixture.BuildServices(TimeProvider.System);
        await using var instanceB = fixture.BuildServices(TimeProvider.System);
        var storeA = instanceA.GetRequiredService<IReviewerAccessStore>();
        var storeB = instanceB.GetRequiredService<IReviewerAccessStore>();
        const string username = "pg-same-code";
        var step = Totp.StepAt(Start);

        var opened = await Task.WhenAll(
            storeA.OpenSessionAsync(Session(username, Start), step, None),
            storeB.OpenSessionAsync(Session(username, Start), step, None));

        Assert.Single(opened, wasOpened => wasOpened);
        Assert.Equal(step, (await storeA.GetGuardAsync(username, None)).LastTotpStep);
        Assert.False(await storeB.OpenSessionAsync(Session(username, Start), step - 1, None));
        Assert.True(await storeB.OpenSessionAsync(Session(username, Start), step + 1, None));
        Assert.Equal(2, (await storeA.ListEventsAsync(username, 10, None)).Count(entry => entry.Kind == ReviewerAccessEventKind.SignedIn));
    }

    [Fact]
    public async Task Failures_PauseSignInAtTheLimit_AndEveryAttemptIsLogged()
    {
        await using var services = fixture.BuildServices(TimeProvider.System);
        var store = services.GetRequiredService<IReviewerAccessStore>();
        const string username = "pg-lockout";
        var pausedUntil = Start.AddMinutes(15);

        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => store.RecordFailureAsync(username, Start, 3, pausedUntil, None)));

        Assert.Single(results, startedPause => startedPause);
        Assert.Equal(new ReviewerGuard(0, pausedUntil, 0), await store.GetGuardAsync(username, None));
        Assert.False(await store.OpenSessionAsync(Session(username, Start.AddMinutes(14)), 1, None));
        await store.RecordBlockedAsync(username, Start.AddMinutes(14), None);
        Assert.True(await store.OpenSessionAsync(Session(username, pausedUntil), 1, None));
        Assert.Equal(ReviewerGuard.Fresh with { LastTotpStep = 1 }, await store.GetGuardAsync(username, None));

        var kinds = (await store.ListEventsAsync(username, 10, None)).Select(entry => entry.Kind).ToList();
        Assert.Equal(ReviewerAccessEventKind.SignedIn, kinds[0]);
        Assert.Equal(ReviewerAccessEventKind.SignInBlocked, kinds[1]);
        Assert.Equal(3, kinds.Count(kind => kind == ReviewerAccessEventKind.SignInFailed));
        Assert.Single(kinds, kind => kind == ReviewerAccessEventKind.LockedOut);
    }

    [Fact]
    public async Task Sessions_AreFoundByTokenHash_UsedAndEndedOnce()
    {
        await using var services = fixture.BuildServices(TimeProvider.System);
        var store = services.GetRequiredService<IReviewerAccessStore>();
        const string username = "pg-sessions";
        var session = Session(username, Start);
        Assert.True(await store.OpenSessionAsync(session, Totp.StepAt(Start), None));

        var found = await store.FindSessionAsync(session.TokenSha256, None);
        Assert.Equal((session.SessionId, username, Start, Start, session.ExpiresAt, (DateTimeOffset?)null), (found!.SessionId, found.Username, found.CreatedAt, found.LastSeenAt, found.ExpiresAt, found.EndedAt));
        Assert.Equal(session.CredentialFingerprint, found.CredentialFingerprint);

        await store.TouchSessionAsync(session.SessionId, Start.AddMinutes(5), None);
        await store.TouchSessionAsync(session.SessionId, Start.AddMinutes(2), None); // never moves back
        Assert.Equal(Start.AddMinutes(5), (await store.FindSessionAsync(session.TokenSha256, None))!.LastSeenAt);

        Assert.True(await store.EndSessionAsync(session.SessionId, Start.AddMinutes(6), None));
        Assert.False(await store.EndSessionAsync(session.SessionId, Start.AddMinutes(7), None));
        await store.TouchSessionAsync(session.SessionId, Start.AddMinutes(8), None);

        var ended = (await store.FindSessionAsync(session.TokenSha256, None))!;
        Assert.Equal((Start.AddMinutes(5), (DateTimeOffset?)Start.AddMinutes(6)), (ended.LastSeenAt, ended.EndedAt));
        Assert.Null(await store.FindSessionAsync(SHA256.HashData([1, 2, 3]), None));
        var signedOut = (await store.ListEventsAsync(username, 10, None))[0];
        Assert.Equal((ReviewerAccessEventKind.SignedOut, (Guid?)session.SessionId), (signedOut.Kind, signedOut.SessionId));
    }

    private static ReviewerSession Session(string username, DateTimeOffset at) =>
        new(Guid.NewGuid(), RandomNumberGenerator.GetBytes(32), username, RandomNumberGenerator.GetBytes(32), at, at, at.AddHours(12), EndedAt: null);
}
