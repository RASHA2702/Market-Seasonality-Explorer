using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FiatCryptoGateway.Api.Contracts;
using FiatCryptoGateway.Application.Reviewers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace FiatCryptoGateway.Api.Tests;

public sealed class ReviewerSignInTests(GatewayApiFactory factory) : IClassFixture<GatewayApiFactory>
{
    private static readonly Uri SignInPath = new("/v1/review/sign-in", UriKind.Relative);
    private static readonly Uri QueuePath = new("/v1/review/queue", UriKind.Relative);

    [Fact]
    public async Task SignIn_OpensASessionForReviewerEndpoints_ThatSigningOutEnds()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 26, 6, 0, 0, TimeSpan.Zero));
        using var host = Host(clock);
        using var browser = host.CreateClient();

        using var response = await browser.PostAsJsonAsync(
            SignInPath, new { username = "Compliance-Officer", password = TestReviewer.Password, code = CodeAt(clock) });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        var signIn = (await response.Content.ReadFromJsonAsync<ReviewerSignInResponse>())!;
        Assert.Equal(
            (TestReviewer.Username, TestReviewer.Username, 30, clock.GetUtcNow().AddHours(12), (DateTimeOffset?)null, 0),
            (signIn.Reviewer.Username, signIn.Reviewer.DisplayName, signIn.IdleMinutes, signIn.ExpiresAt, signIn.PreviousSignInAt, signIn.FailedAttemptsSincePrevious));

        using var reviewer = host.CreateClient();
        reviewer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", signIn.Token);
        using var queue = await reviewer.GetAsync(QueuePath);
        Assert.Equal(HttpStatusCode.OK, queue.StatusCode);

        using var signOut = await reviewer.PostAsync(new Uri("/v1/review/sign-out", UriKind.Relative), content: null);
        Assert.Equal(HttpStatusCode.NoContent, signOut.StatusCode);
        using var afterSignOut = await reviewer.GetAsync(QueuePath);
        Assert.Equal(HttpStatusCode.Unauthorized, afterSignOut.StatusCode);
    }

    [Fact]
    public async Task SignIn_FailsTheSameWayWhateverWasWrong()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 26, 6, 0, 0, TimeSpan.Zero));
        using var host = Host(clock);
        using var browser = host.CreateClient();
        var code = CodeAt(clock);

        var answers = new List<string>();
        foreach (object attempt in new object[]
        {
            new { username = TestReviewer.Username, password = "not the password at all", code },
            new { username = TestReviewer.Username, password = TestReviewer.Password, code = "000000" },
            new { username = "nobody", password = TestReviewer.Password, code },
            new { },
        })
        {
            using var response = await browser.PostAsJsonAsync(SignInPath, attempt);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
            answers.Add(await response.Content.ReadAsStringAsync());
        }

        Assert.Contains("SIGN_IN_FAILED", Assert.Single(answers.Distinct()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MerchantKeys_AreNotSessions_AndEndedSessionsAreRefused()
    {
        using var merchantKeyAsSession = factory.CreateClientFor(GatewayApiFactory.ClientAKey);
        using var madeUp = factory.CreateClientFor(ReviewerAccessService.TokenPrefix + new string('A', 43));

        using var merchantOnQueue = await merchantKeyAsSession.GetAsync(QueuePath);
        using var madeUpOnQueue = await madeUp.GetAsync(QueuePath);
        using var madeUpOnQuotes = await madeUp.PostAsJsonAsync(
            new Uri("/v1/quotes", UriKind.Relative), new { sourceCurrency = "INR", sourceAmount = "10000.00", destinationAsset = "USDC@ethereum" });

        Assert.Equal(HttpStatusCode.Forbidden, merchantOnQueue.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, madeUpOnQueue.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, madeUpOnQuotes.StatusCode);
    }

    [Fact]
    public async Task IdleSession_IsRefused_AndTheNextSignInShowsThePreviousOne()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 26, 6, 0, 0, TimeSpan.Zero));
        using var host = Host(clock);
        var firstSignIn = clock.GetUtcNow();
        using var reviewer = await host.SignInReviewerAsync();

        clock.Advance(TimeSpan.FromMinutes(31));
        using var idle = await reviewer.GetAsync(QueuePath);
        Assert.Equal(HttpStatusCode.Unauthorized, idle.StatusCode);

        using var browser = host.CreateClient();
        using var response = await browser.PostAsJsonAsync(
            SignInPath, new { username = TestReviewer.Username, password = TestReviewer.Password, code = CodeAt(clock) });
        var signIn = (await response.Content.ReadFromJsonAsync<ReviewerSignInResponse>())!;
        Assert.Equal((firstSignIn, 0), (signIn.PreviousSignInAt!.Value, signIn.FailedAttemptsSincePrevious));
    }

    [Fact]
    public async Task SignInAttempts_AreLimitedPerAddress()
    {
        using var host = factory.WithWebHostBuilder(builder => builder.UseSetting("Gateway:Review:SignInAttemptsPerMinute", "2"));
        using var browser = host.CreateClient();
        var wrong = new { username = TestReviewer.Username, password = "not the password at all", code = "000000" };

        using var first = await browser.PostAsJsonAsync(SignInPath, wrong);
        using var second = await browser.PostAsJsonAsync(SignInPath, wrong);
        using var third = await browser.PostAsJsonAsync(SignInPath, wrong);

        Assert.Equal([HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests], new[] { first.StatusCode, second.StatusCode, third.StatusCode });
        Assert.Equal("60", third.Headers.GetValues("Retry-After").Single());
        Assert.Contains("TOO_MANY_ATTEMPTS", await third.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    private static string CodeAt(TimeProvider clock) => TestReviewer.Code(Totp.StepAt(clock.GetUtcNow()));

    private WebApplicationFactory<Program> Host(ManualTimeProvider clock) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(clock)));
}
