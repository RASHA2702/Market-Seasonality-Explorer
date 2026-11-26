using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;

namespace FiatCryptoGateway.Api;

/// <summary>The HTTPS proxy in front of the API (Caddy in the container setup). Section <c>Gateway:Proxy</c>.</summary>
public sealed class ProxyOptions
{
    public const string SectionName = "Gateway:Proxy";

    /// <summary>
    /// Comma-separated networks (CIDR) of the proxies allowed to say, with <c>X-Forwarded-For</c> and
    /// <c>X-Forwarded-Proto</c>, which client and scheme a request came from. Empty (the default) means the API is
    /// reached directly and ignores those headers, so no client can choose the address it is rate-limited by.
    /// </summary>
    public string TrustedNetworks { get; set; } = string.Empty;

    public IReadOnlyList<string> Entries => TrustedNetworks.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

public sealed class ProxyOptionsValidator : IValidateOptions<ProxyOptions>
{
    public ValidateOptionsResult Validate(string? name, ProxyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        foreach (var entry in options.Entries)
        {
            if (!System.Net.IPNetwork.TryParse(entry, out var network))
            {
                failures.Add($"Proxy TrustedNetworks entry '{entry}' is not a network such as 172.30.10.0/24.");
            }
            else if (network.PrefixLength == 0)
            {
                failures.Add($"Proxy TrustedNetworks entry '{entry}' would trust every address, letting any client claim to be any other.");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

public static class ProxyServiceCollectionExtensions
{
    /// <summary>
    /// Takes the client's address and scheme from the proxy's <c>X-Forwarded-*</c> headers, but only for requests that
    /// arrive from a trusted proxy network, and only the proxy's own entry: what it saw, never what a client wrote.
    /// The usual default of trusting this machine (loopback) is dropped, so nothing is trusted unless configured.
    /// </summary>
    public static IServiceCollection AddProxySupport(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<ProxyOptions>().Bind(configuration.GetSection(ProxyOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<ProxyOptions>, ProxyOptionsValidator>();
        services.AddOptions<ForwardedHeadersOptions>().Configure<IOptions<ProxyOptions>>((forwarded, proxy) =>
        {
            // With no known proxies at all, ASP.NET Core would trust every sender, so without a configured proxy the
            // headers are not read at all.
            forwarded.ForwardedHeaders = proxy.Value.Entries.Count == 0
                ? ForwardedHeaders.None
                : ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            forwarded.ForwardLimit = 1;
            forwarded.KnownProxies.Clear();
            forwarded.KnownIPNetworks.Clear();
            foreach (var entry in proxy.Value.Entries)
            {
                forwarded.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(entry));
            }
        });
        return services;
    }
}
