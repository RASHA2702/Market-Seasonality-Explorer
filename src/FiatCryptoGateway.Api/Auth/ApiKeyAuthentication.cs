using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using FiatCryptoGateway.Application.Ports;
using FiatCryptoGateway.Application.Reviewers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace FiatCryptoGateway.Api.Auth;

public sealed class ApiKeyClient
{
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Lower- or upper-case hex SHA-256 of the API key. The key itself is never stored.</summary>
    public string KeySha256 { get; set; } = string.Empty;

    /// <summary>
    /// <c>Merchant</c>, the only role an API key can have. Reviewers sign in to the console with a password and an
    /// authenticator code instead (<see cref="ReviewerAccessOptions"/>).
    /// </summary>
    public string Role { get; set; } = nameof(ApiClientRole.Merchant);

    /// <summary>The name customers see on the checkout page (merchants only). Defaults to the client ID.</summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Where the checkout page sends a customer back to when the payment is done (merchants only): HTTPS, or HTTP to
    /// this machine for local testing. The order ID is added as <c>?order=</c>; the merchant must still check the
    /// order through the API, because anyone can open that address.
    /// </summary>
    public Uri? CheckoutReturnUrl { get; set; }

    /// <summary>Where order status changes are posted (merchants only). HTTPS, or HTTP to this machine for local testing.</summary>
    public Uri? WebhookUrl { get; set; }

    /// <summary>
    /// The secret notifications are signed with, at least 32 characters, shared with the merchant out of band.
    /// Supply it through an environment variable or a secret store, never in a committed file.
    /// </summary>
    public string? WebhookSecret { get; set; }
}

public static class ApiPolicies
{
    public const string Merchant = nameof(ApiClientRole.Merchant);
    public const string Reviewer = nameof(ApiClientRole.Reviewer);
}

public sealed class ApiKeyOptions
{
    public const string SectionName = "Gateway:ApiKeys";

    public IList<ApiKeyClient> Clients { get; } = new List<ApiKeyClient>();
}

public sealed class ApiKeyOptionsValidator(IOptions<ReviewerAccessOptions> reviewers) : IValidateOptions<ApiKeyOptions>
{
    public ValidateOptionsResult Validate(string? name, ApiKeyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        var reviewerUsernames = ReviewerUsernames().ToHashSet(StringComparer.Ordinal);
        foreach (var client in options.Clients)
        {
            if (string.IsNullOrWhiteSpace(client.ClientId))
            {
                failures.Add("Every API client needs a ClientId.");
            }

            if (client.KeySha256.Length != 64 || !client.KeySha256.All(char.IsAsciiHexDigit))
            {
                failures.Add($"API client '{client.ClientId}' needs KeySha256: the 64-character hex SHA-256 of its key.");
            }

            if (client.Role == nameof(ApiClientRole.Reviewer))
            {
                failures.Add(
                    $"API client '{client.ClientId}' has the Reviewer role, which API keys no longer take: reviewers sign in with a " +
                    "password and an authenticator code. Remove this client and set up a reviewer account (README, Reviewer accounts).");
            }
            else if (client.Role != nameof(ApiClientRole.Merchant))
            {
                failures.Add($"API client '{client.ClientId}' has role '{client.Role}'; use Merchant.");
            }

            if (reviewerUsernames.Contains(client.ClientId))
            {
                failures.Add($"API client '{client.ClientId}' has the same name as a reviewer; give them different names so no log can confuse them.");
            }

            ValidateWebhook(client, failures);
            ValidateCheckout(client, failures);
        }

        if (options.Clients.Select(client => client.ClientId).Distinct(StringComparer.Ordinal).Count() != options.Clients.Count)
        {
            failures.Add("API client IDs must be unique.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    /// <summary>Reviewer settings that are themselves invalid are reported by their own check, not twice.</summary>
    private IEnumerable<string> ReviewerUsernames()
    {
        try
        {
            return [.. reviewers.Value.Reviewers.Select(reviewer => reviewer.Username)];
        }
        catch (OptionsValidationException)
        {
            return [];
        }
    }

    private static void ValidateCheckout(ApiKeyClient client, List<string> failures)
    {
        if (client.DisplayName is { } name && (string.IsNullOrWhiteSpace(name) || name.Length > 100 || name.Any(char.IsControl)))
        {
            failures.Add($"API client '{client.ClientId}' needs a DisplayName of 1 to 100 printable characters, or none.");
        }

        if (client.CheckoutReturnUrl is null)
        {
            return;
        }

        if (client.Role != nameof(ApiClientRole.Merchant))
        {
            failures.Add($"API client '{client.ClientId}' is not a merchant, so it has no checkout to return to.");
        }

        if (!IsSafeCallbackUrl(client.CheckoutReturnUrl))
        {
            failures.Add($"API client '{client.ClientId}' needs CheckoutReturnUrl: an absolute HTTPS URL without credentials (HTTP only to localhost).");
        }
    }

    private static bool IsSafeCallbackUrl(Uri? url) =>
        url is { IsAbsoluteUri: true } &&
        (url.Scheme == Uri.UriSchemeHttps || (url.Scheme == Uri.UriSchemeHttp && url.IsLoopback)) &&
        string.IsNullOrEmpty(url.UserInfo);

    // Messages name the client and the rule, never the URL's query or the secret.
    private static void ValidateWebhook(ApiKeyClient client, List<string> failures)
    {
        if (client.WebhookUrl is null && client.WebhookSecret is null)
        {
            return;
        }

        if (client.Role != nameof(ApiClientRole.Merchant))
        {
            failures.Add($"API client '{client.ClientId}' is not a merchant, so it cannot receive webhooks.");
        }

        if (!IsSafeCallbackUrl(client.WebhookUrl))
        {
            failures.Add($"API client '{client.ClientId}' needs WebhookUrl: an absolute HTTPS URL without credentials (HTTP only to localhost).");
        }

        if (client.WebhookSecret is not { Length: >= 32 } secret || string.IsNullOrWhiteSpace(secret))
        {
            failures.Add($"API client '{client.ClientId}' needs WebhookSecret: at least 32 characters, from an environment variable or secret store.");
        }
    }
}

/// <summary>
/// Authenticates a merchant's <c>Authorization: Bearer &lt;key&gt;</c> by comparing the key's SHA-256 with configured
/// hashes in constant time. Each key maps to one client ID; every resource is scoped to that client.
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptions<ApiKeyOptions> keys)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";
    public const string ClientIdClaim = "client_id";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (GatewayAuthentication.BearerToken(Request) is not { } key)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var presented = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        foreach (var client in keys.Value.Clients)
        {
            if (CryptographicOperations.FixedTimeEquals(presented, Convert.FromHexString(client.KeySha256)))
            {
                var identity = new ClaimsIdentity([new Claim(ClientIdClaim, client.ClientId), new Claim(ClaimTypes.Role, client.Role)], SchemeName);
                return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
            }
        }

        return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));
    }
}

public static class ApiKeyAuthenticationExtensions
{
    /// <summary>Merchants authenticate with API keys; reviewers with the session they open by signing in.</summary>
    public static IServiceCollection AddGatewayAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<ApiKeyOptions>().Bind(configuration.GetSection(ApiKeyOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<ApiKeyOptions>, ApiKeyOptionsValidator>();
        services.AddAuthentication(GatewayAuthentication.Scheme)
            .AddPolicyScheme(GatewayAuthentication.Scheme, displayName: null, options =>
                options.ForwardDefaultSelector = context =>
                    ReviewerAccessService.IsSessionToken(GatewayAuthentication.BearerToken(context.Request))
                        ? ReviewerSessionAuthenticationHandler.SchemeName
                        : ApiKeyAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, configureOptions: null)
            .AddScheme<AuthenticationSchemeOptions, ReviewerSessionAuthenticationHandler>(ReviewerSessionAuthenticationHandler.SchemeName, configureOptions: null);
        services.AddAuthorizationBuilder()
            .AddPolicy(ApiPolicies.Merchant, policy => policy.RequireRole(ApiPolicies.Merchant))
            .AddPolicy(ApiPolicies.Reviewer, policy => policy.RequireRole(ApiPolicies.Reviewer));
        return services;
    }

    public static string GetClientId(this ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return user.FindFirstValue(ApiKeyAuthenticationHandler.ClientIdClaim)
            ?? throw new InvalidOperationException("An authenticated request must carry a client ID.");
    }
}
