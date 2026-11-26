using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using FiatCryptoGateway.Api.Contracts;
using FiatCryptoGateway.Application.Reviewers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace FiatCryptoGateway.Api.Tests;

/// <summary>The reviewer account every test host has. Test-only values, not secrets.</summary>
internal static class TestReviewer
{
    public const string Username = "compliance-officer";
    public const string Password = "test-only reviewer passphrase";

    // Made at run time, so no key-like text sits in the repository.
    public static readonly string Secret = Base32.Encode([.. Enumerable.Range(1, Totp.SecretBytes).Select(value => (byte)value)]);

    private static readonly Lazy<string> PasswordHash = new(() => PasswordHashing.Hash(Password));
    private static readonly ConditionalWeakTable<IServiceProvider, Session> Sessions = new();

    public static IReadOnlyList<KeyValuePair<string, string?>> Settings =>
    [
        new("Gateway:Review:Reviewers:0:Username", Username),
        new("Gateway:Review:Reviewers:0:PasswordHash", PasswordHash.Value),
        new("Gateway:Review:Reviewers:0:TotpSecret", Secret),
    ];

    public static string Code(long step) => Totp.Code(Base32.TryDecode(Secret), step);

    /// <summary>
    /// A client signed in as the test reviewer. Each host keeps one session while it lasts, because an authenticator
    /// code works only once.
    /// </summary>
    public static async Task<HttpClient> SignInReviewerAsync(this WebApplicationFactory<Program> host)
    {
        ArgumentNullException.ThrowIfNull(host);
        var token = await TokenAsync(host);
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<string> TokenAsync(WebApplicationFactory<Program> host)
    {
        var services = host.Services;
        Sessions.TryGetValue(services, out var cached);
        if (cached is not null && await services.GetRequiredService<ReviewerAccessService>().AuthenticateAsync(cached.Token, CancellationToken.None) is not null)
        {
            return cached.Token;
        }

        var step = Math.Max(Totp.StepAt(services.GetRequiredService<TimeProvider>().GetUtcNow()), (cached?.Step ?? 0) + 1);
        using var client = host.CreateClient();
        using var response = await client.PostAsJsonAsync(
            new Uri("/v1/review/sign-in", UriKind.Relative), new { username = Username, password = Password, code = Code(step) });
        response.EnsureSuccessStatusCode();
        var signIn = (await response.Content.ReadFromJsonAsync<ReviewerSignInResponse>())!;
        Sessions.AddOrUpdate(services, new Session(signIn.Token, step));
        return signIn.Token;
    }

    private sealed record Session(string Token, long Step);
}
