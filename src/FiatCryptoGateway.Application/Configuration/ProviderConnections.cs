using Microsoft.Extensions.Options;

namespace FiatCryptoGateway.Application.Configuration;

public enum ProviderEnvironment
{
    Test,
    Live,
}

/// <summary>
/// Connection settings for one external provider, for example <c>Gateway:Providers:onmeta</c>. Secrets are
/// supplied through environment variables or a secret store (for example
/// <c>Gateway__Providers__onmeta__ApiKey</c>), never committed.
/// </summary>
public sealed class ProviderConnectionOptions
{
    public ProviderEnvironment Environment { get; set; } = ProviderEnvironment.Test;

    public Uri? BaseUrl { get; set; }

    public string? ApiKey { get; set; }

    public string? WebhookSecret { get; set; }
}

/// <summary>Bound from the <c>Gateway</c> section, so each provider lives at <c>Gateway:Providers:&lt;name&gt;</c>.</summary>
public sealed class ProviderConnectionsOptions
{
    public IDictionary<string, ProviderConnectionOptions> Providers { get; } =
        new Dictionary<string, ProviderConnectionOptions>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// What a real provider adapter declares, so the guard can tell its test environment from its live one:
/// the hosts of its test environment and the prefixes its live keys carry.
/// </summary>
public sealed record ProviderEnvironmentProfile(
    string Name,
    IReadOnlyCollection<string> TestHosts,
    IReadOnlyCollection<string> LiveKeyPrefixes);

/// <summary>
/// Refuses live credentials outside production. Every configured provider must have an installed adapter,
/// be set to <see cref="ProviderEnvironment.Test"/>, point at one of its declared test hosts over HTTPS, and
/// carry no key that looks live. This build cannot run in production at all (the safety gate stops it), so the
/// guard always applies; whoever adds a production mode must allow live credentials there and only there.
/// Error messages name the provider and the rule, never a key's value.
/// </summary>
public sealed class LiveCredentialGuard(IEnumerable<ProviderEnvironmentProfile> profiles) : IValidateOptions<ProviderConnectionsOptions>
{
    private static readonly string[] CommonLiveKeyPrefixes = ["sk_live_", "pk_live_", "rk_live_", "live_", "prod_"];

    public ValidateOptionsResult Validate(string? name, ProviderConnectionsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        foreach (var (provider, connection) in options.Providers)
        {
            if (connection.Environment != ProviderEnvironment.Test)
            {
                failures.Add($"Provider '{provider}' is set to {connection.Environment}; outside production only Test is allowed.");
            }

            var profile = profiles.FirstOrDefault(candidate => string.Equals(candidate.Name, provider, StringComparison.OrdinalIgnoreCase));
            if (profile is null)
            {
                failures.Add($"No adapter is installed for provider '{provider}'; remove its settings or install the adapter.");
                continue;
            }

            if (connection.BaseUrl is null ||
                connection.BaseUrl.Scheme != Uri.UriSchemeHttps ||
                !profile.TestHosts.Contains(connection.BaseUrl.Host, StringComparer.OrdinalIgnoreCase))
            {
                failures.Add(
                    $"Provider '{provider}' must use its HTTPS test environment ({string.Join(", ", profile.TestHosts)}).");
            }

            if (connection.ApiKey is { Length: > 0 } key &&
                CommonLiveKeyPrefixes.Concat(profile.LiveKeyPrefixes).Any(prefix => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                failures.Add($"Provider '{provider}' has an API key that looks like a live key; only test keys are accepted.");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
