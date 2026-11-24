using FiatCryptoGateway.Application.Configuration;
using Microsoft.Extensions.Options;

namespace FiatCryptoGateway.Tests;

public sealed class LiveCredentialGuardTests
{
    private static readonly ProviderEnvironmentProfile Partner = new(
        "examplepartner", TestHosts: ["api.sandbox.example.com"], LiveKeyPrefixes: ["ep_live_"]);

    [Fact]
    public void TestEnvironmentWithTestKey_IsAccepted()
    {
        var result = Validate(Connection());

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void LiveEnvironment_IsRefused()
    {
        var result = Validate(Connection(environment: ProviderEnvironment.Live));

        Assert.Contains("only Test is allowed", result.FailureMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://api.example.com")]          // the live host
    [InlineData("http://api.sandbox.example.com")]   // not HTTPS
    public void NonTestEndpoint_IsRefused(string baseUrl)
    {
        var result = Validate(Connection(baseUrl: baseUrl));

        Assert.Contains("HTTPS test environment", result.FailureMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ep_live_123")]
    [InlineData("sk_live_123")]
    public void LiveLookingKey_IsRefusedWithoutRevealingIt(string key)
    {
        var result = Validate(Connection(apiKey: key));

        Assert.Contains("looks like a live key", result.FailureMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(key, result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderWithoutAnInstalledAdapter_IsRefused()
    {
        var options = new ProviderConnectionsOptions();
        options.Providers["unknownpartner"] = new ProviderConnectionOptions { BaseUrl = new Uri("https://api.sandbox.example.com") };

        var result = new LiveCredentialGuard([Partner]).Validate(null, options);

        Assert.Contains("No adapter is installed", result.FailureMessage, StringComparison.Ordinal);
    }

    private static ValidateOptionsResult Validate(ProviderConnectionOptions connection)
    {
        var options = new ProviderConnectionsOptions();
        options.Providers["examplepartner"] = connection;
        return new LiveCredentialGuard([Partner]).Validate(null, options);
    }

    private static ProviderConnectionOptions Connection(
        ProviderEnvironment environment = ProviderEnvironment.Test,
        string baseUrl = "https://api.sandbox.example.com",
        string apiKey = "ep_test_123") =>
        new() { Environment = environment, BaseUrl = new Uri(baseUrl), ApiKey = apiKey };
}
