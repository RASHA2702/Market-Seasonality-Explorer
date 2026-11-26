using FiatCryptoGateway.Application.Reviewers;
using FiatCryptoGateway.Sandbox;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FiatCryptoGateway.Tests;

public sealed class ReviewerAccessTests
{
    private const string Username = "compliance-officer";
    private const string Password = "correct horse battery staple";
    private static readonly CancellationToken None = CancellationToken.None;

    // A weak hash keeps these tests fast; startup refuses one like it (see ReviewerSetupTests).
    private static readonly string FastHash = PasswordHashing.Hash(Password, iterations: 1_000);
    private static readonly string Secret = Base32.Encode([.. Enumerable.Range(1, Totp.SecretBytes).Select(value => (byte)value)]);

    private readonly ManualTimeProvider _time = new(Fixtures.Start);
    private readonly InMemoryReviewerAccessStore _store = new();
    private readonly ReviewerAccessOptions _options = new();

    public ReviewerAccessTests()
    {
        _options.Reviewers.Add(new ReviewerAccount { Username = Username, DisplayName = "Duty reviewer", PasswordHash = FastHash, TotpSecret = Secret });
    }

    [Fact]
    public async Task SignIn_WithPasswordAndCode_OpensASessionForThatReviewer()
    {
        var access = Access();

        var signIn = await access.SignInAsync("Compliance-Officer ", Password, Code(), None);

        Assert.NotNull(signIn);
        Assert.StartsWith(ReviewerAccessService.TokenPrefix, signIn.Token, StringComparison.Ordinal);
        Assert.Equal(47, signIn.Token.Length);
        Assert.Equal((Username, "Duty reviewer"), (signIn.Reviewer.Username, signIn.Reviewer.DisplayName));
        Assert.Equal(_time.GetUtcNow().AddHours(12), signIn.Reviewer.ExpiresAt);
        Assert.Null(signIn.PreviousSignInAt);
        Assert.Equal(signIn.Reviewer, await access.AuthenticateAsync(signIn.Token, None));
        Assert.Equal(ReviewerAccessEventKind.SignedIn, Assert.Single(await Events()).Kind);
    }

    [Fact]
    public async Task SignIn_FailsTheSameWayForEveryWrongPart()
    {
        var access = Access();

        Assert.Null(await access.SignInAsync(Username, "wrong password here", Code(), None));
        Assert.Null(await access.SignInAsync(Username, Password, "000000", None));
        Assert.Null(await access.SignInAsync(Username, Password, null, None));
        Assert.Null(await access.SignInAsync(Username, null, Code(), None));
        Assert.Null(await access.SignInAsync("someone-else", Password, Code(), None));
        Assert.Null(await access.SignInAsync(null, Password, Code(), None));

        // Failures for the real reviewer are counted and logged; a username nobody has leaves no trace.
        Assert.Equal(4, (await _store.GetGuardAsync(Username, None)).FailedAttempts);
        Assert.All(await Events(), entry => Assert.Equal(ReviewerAccessEventKind.SignInFailed, entry.Kind));
        Assert.Empty(await _store.ListEventsAsync("someone-else", 10, None));
    }

    [Fact]
    public async Task Code_WorksOnlyOnce()
    {
        var access = Access();
        var code = Code();

        Assert.NotNull(await access.SignInAsync(Username, Password, code, None));
        Assert.Null(await access.SignInAsync(Username, Password, code, None));

        _time.Advance(TimeSpan.FromSeconds(Totp.StepSeconds));
        Assert.NotNull(await access.SignInAsync(Username, Password, Code(), None));
    }

    [Fact]
    public async Task RepeatedFailures_PauseSignIn_ThenTheReviewerIsToldOnNextSignIn()
    {
        var access = Access();
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            Assert.Null(await access.SignInAsync(Username, "wrong password here", Code(), None));
        }

        // Paused: even the right password and code fail, and trying does not extend the pause.
        Assert.Null(await access.SignInAsync(Username, Password, Code(), None));
        Assert.Equal(_time.GetUtcNow().AddMinutes(15), (await _store.GetGuardAsync(Username, None)).LockedUntil);

        _time.Advance(TimeSpan.FromMinutes(15));
        var signIn = await access.SignInAsync(Username, Password, Code(), None);

        Assert.NotNull(signIn);
        Assert.Null(signIn.PreviousSignInAt);
        Assert.Equal(6, signIn.FailedAttemptsSincePrevious);
        Assert.Equal(ReviewerGuard.Fresh with { LastTotpStep = Totp.StepAt(_time.GetUtcNow()) }, await _store.GetGuardAsync(Username, None));
        Assert.Equal(
            [
                ReviewerAccessEventKind.SignedIn, ReviewerAccessEventKind.SignInBlocked, ReviewerAccessEventKind.LockedOut,
                ReviewerAccessEventKind.SignInFailed, ReviewerAccessEventKind.SignInFailed, ReviewerAccessEventKind.SignInFailed,
                ReviewerAccessEventKind.SignInFailed, ReviewerAccessEventKind.SignInFailed,
            ],
            (await Events()).Select(entry => entry.Kind));

        var firstSignInAt = _time.GetUtcNow();
        _time.Advance(TimeSpan.FromHours(1));
        var next = await access.SignInAsync(Username, Password, Code(), None);
        Assert.Equal((firstSignInAt, 0), (next!.PreviousSignInAt, next.FailedAttemptsSincePrevious));
    }

    [Fact]
    public async Task FailuresInARow_AreCountedAfresh_AfterASuccess()
    {
        var access = Access();
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            Assert.Null(await access.SignInAsync(Username, "wrong password here", Code(), None));
        }

        Assert.NotNull(await access.SignInAsync(Username, Password, Code(), None));
        _time.Advance(TimeSpan.FromSeconds(Totp.StepSeconds));
        Assert.Null(await access.SignInAsync(Username, "wrong password here", Code(), None));

        Assert.NotNull(await access.SignInAsync(Username, Password, Code(), None));
    }

    [Fact]
    public async Task Session_EndsAfterThirtyIdleMinutes()
    {
        var access = Access();
        var token = (await access.SignInAsync(Username, Password, Code(), None))!.Token;

        _time.Advance(TimeSpan.FromMinutes(29));
        Assert.NotNull(await access.AuthenticateAsync(token, None));
        _time.Advance(TimeSpan.FromMinutes(29)); // used 29 minutes ago
        Assert.NotNull(await access.AuthenticateAsync(token, None));
        _time.Advance(TimeSpan.FromMinutes(31));
        Assert.Null(await access.AuthenticateAsync(token, None));
    }

    [Fact]
    public async Task Session_EndsAfterTwelveHoursHoweverActive()
    {
        var access = Access();
        var token = (await access.SignInAsync(Username, Password, Code(), None))!.Token;

        for (var minutes = 20; minutes < 12 * 60; minutes += 20)
        {
            _time.Advance(TimeSpan.FromMinutes(20));
            Assert.NotNull(await access.AuthenticateAsync(token, None));
        }

        _time.Advance(TimeSpan.FromMinutes(20));
        Assert.Null(await access.AuthenticateAsync(token, None));
    }

    [Fact]
    public async Task SigningOut_EndsTheSessionOnce()
    {
        var access = Access();
        var token = (await access.SignInAsync(Username, Password, Code(), None))!.Token;
        var reviewer = (await access.AuthenticateAsync(token, None))!;

        await access.SignOutAsync(reviewer, None);
        await access.SignOutAsync(reviewer, None);

        Assert.Null(await access.AuthenticateAsync(token, None));
        Assert.Equal(
            [ReviewerAccessEventKind.SignedOut, ReviewerAccessEventKind.SignedIn],
            (await Events()).Select(entry => entry.Kind));
    }

    [Fact]
    public async Task Session_Ends_WhenTheAccountIsRemovedOrItsCredentialsChange()
    {
        var access = Access();
        var token = (await access.SignInAsync(Username, Password, Code(), None))!.Token;

        _options.Reviewers[0].PasswordHash = PasswordHashing.Hash(Password, iterations: 1_000);
        Assert.Null(await access.AuthenticateAsync(token, None));

        _options.Reviewers[0].PasswordHash = FastHash;
        Assert.NotNull(await access.AuthenticateAsync(token, None));

        _options.Reviewers[0].TotpSecret = Base32.Encode(new byte[Totp.SecretBytes]);
        Assert.Null(await access.AuthenticateAsync(token, None));

        _options.Reviewers[0].TotpSecret = Secret;
        _options.Reviewers.Clear();
        Assert.Null(await access.AuthenticateAsync(token, None));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("rvs_")]
    [InlineData("rvs_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("test-only-key-client-a")]
    public async Task UnknownTokens_AreNotSessions(string? token)
    {
        Assert.Null(await Access().AuthenticateAsync(token, None));
    }

    private ReviewerAccessService Access() =>
        new(_store, Options.Create(_options), _time, NullLogger<ReviewerAccessService>.Instance);

    private string Code() => Totp.Code(Base32.TryDecode(Secret), Totp.StepAt(_time.GetUtcNow()));

    private Task<IReadOnlyList<ReviewerAccessEvent>> Events() => _store.ListEventsAsync(Username, 100, None);
}
