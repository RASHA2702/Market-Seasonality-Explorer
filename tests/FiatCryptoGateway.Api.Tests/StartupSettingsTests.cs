using FiatCryptoGateway.Application.Reviewers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FiatCryptoGateway.Api.Tests;

/// <summary>
/// Unsafe or invalid settings must stop the API at startup. These tests register exactly what the API registers
/// (its appsettings.json plus the test clients) and run the checks the host runs when it starts. They do not
/// start a host: a host that fails to start is disposed on another thread, which made such tests flaky.
/// </summary>
public sealed class StartupSettingsTests
{
    private const string WebhookSecret = "test-only-merchant-webhook-secret-0123456789";

    [Fact]
    public void TestSettings_PassEveryStartupCheck()
    {
        Validate();
    }

    [Theory]
    [InlineData("Gateway:Mode", "Production")]
    [InlineData("Gateway:AllowRealFundsMovement", "true")]
    public void ProductionSettings_StopStartup(string key, string value)
    {
        var exception = Assert.Throws<OptionsValidationException>(() => Validate((key, value)));

        Assert.Contains("sandbox-only", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Gateway:Providers:anypartner:Environment", "Live")]
    [InlineData("Gateway:Providers:anypartner:ApiKey", "sk_live_not_a_real_key")]
    public void LiveProviderSettings_StopStartup(string key, string value)
    {
        var exception = Assert.Throws<OptionsValidationException>(() => Validate((key, value)));

        Assert.DoesNotContain("sk_live_not_a_real_key", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MalformedApiKeyHash_StopsStartup()
    {
        Assert.Throws<OptionsValidationException>(() => Validate(("Gateway:ApiKeys:Clients:0:KeySha256", "not-a-hash")));
    }

    [Theory]
    [InlineData("Gateway:Compliance:Countries", "IND")]
    [InlineData("Gateway:Webhooks:LeaseSeconds", "100")] // shorter than a batch of timeouts
    [InlineData("Gateway:Reconciliation:TimeZone", "Mars/Olympus_Mons")]
    [InlineData("Gateway:Reconciliation:RunAfterHour", "25")]
    [InlineData("Gateway:Settlement:CoolingOffHours", "-1")]
    [InlineData("Gateway:Checkout:BaseUrl", "http://pay.example.com")]
    [InlineData("Gateway:Checkout:LinkSecret", "too-short")]
    [InlineData("Gateway:ApiKeys:Clients:0:CheckoutReturnUrl", "http://merchant.example/return")]
    [InlineData("Gateway:Review:Reviewers:0:TotpSecret", "ABCDEFGH")]                          // far shorter than 160 bits
    [InlineData("Gateway:Review:SessionIdleMinutes", "0")]
    [InlineData("Gateway:Proxy:TrustedNetworks", "0.0.0.0/0")]                                  // trusting everyone lets anyone spoof
    [InlineData("Gateway:Proxy:TrustedNetworks", "172.30.10.0/24, caddy")]
    [InlineData("Gateway:ApiKeys:Clients:0:DisplayName", " ")]
    [InlineData("Gateway:Settlement:Networks", "polygon,mars")]
    public void InvalidLimitsOrDeliverySettings_StopStartup(string key, string value)
    {
        Assert.Throws<OptionsValidationException>(() => Validate((key, value)));
    }

    [Fact]
    public void MerchantWebhook_WithHttpsAndALongSecret_IsAccepted()
    {
        Validate(
            ("Gateway:ApiKeys:Clients:0:WebhookUrl", "https://merchant.example/hooks"),
            ("Gateway:ApiKeys:Clients:0:WebhookSecret", WebhookSecret));
    }

    [Theory]
    [InlineData("http://merchant.example/hooks", WebhookSecret)]            // plain HTTP to another machine
    [InlineData("https://merchant.example/hooks", "too-short")]             // secret under 32 characters
    [InlineData("https://user:password@merchant.example/hooks", WebhookSecret)]
    [InlineData("https://merchant.example/hooks", null)]                    // URL without a secret
    [InlineData(null, WebhookSecret)]                                       // secret without a URL
    public void InvalidWebhookSettings_StopStartup(string? url, string? secret)
    {
        var settings = new List<(string, string)>();
        if (url is not null)
        {
            settings.Add(("Gateway:ApiKeys:Clients:0:WebhookUrl", url));
        }

        if (secret is not null)
        {
            settings.Add(("Gateway:ApiKeys:Clients:0:WebhookSecret", secret));
        }

        var exception = Assert.Throws<OptionsValidationException>(() => Validate([.. settings]));

        Assert.DoesNotContain(WebhookSecret, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalHttpWebhook_IsAcceptedForTesting()
    {
        Validate(
            ("Gateway:ApiKeys:Clients:0:WebhookUrl", "http://localhost:9000/hooks"),
            ("Gateway:ApiKeys:Clients:0:WebhookSecret", WebhookSecret));
    }

    [Fact]
    public void ReviewerApiKey_StopsStartup()
    {
        var exception = Assert.Throws<OptionsValidationException>(() => Validate(
            ("Gateway:ApiKeys:Clients:2:ClientId", "old-reviewer-key"),
            ("Gateway:ApiKeys:Clients:2:KeySha256", GatewayApiFactory.Sha256Hex("test-only-old-reviewer-key")),
            ("Gateway:ApiKeys:Clients:2:Role", "Reviewer")));

        Assert.Contains("reviewer account", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReviewerNamedLikeAMerchant_StopsStartup()
    {
        Assert.Throws<OptionsValidationException>(() => Validate(("Gateway:Review:Reviewers:0:Username", "client-a")));
    }

    [Fact]
    public void WeakReviewerPasswordHash_StopsStartup()
    {
        var exception = Assert.Throws<OptionsValidationException>(() => Validate(
            ("Gateway:Review:Reviewers:0:PasswordHash", PasswordHashing.Hash(TestReviewer.Password, iterations: 100_000))));

        Assert.Contains("PasswordHash", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("pbkdf2-sha256:", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Runs every startup check on the API's registrations with these settings.</summary>
    private static void Validate(params (string Key, string Value)[] overrides)
    {
        var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Gateway:ApiKeys:Clients:0:ClientId"] = "client-a",
            ["Gateway:ApiKeys:Clients:0:KeySha256"] = GatewayApiFactory.Sha256Hex(GatewayApiFactory.ClientAKey),
            ["Gateway:ApiKeys:Clients:1:ClientId"] = "client-b",
            ["Gateway:ApiKeys:Clients:1:KeySha256"] = GatewayApiFactory.Sha256Hex(GatewayApiFactory.ClientBKey),
        };
        foreach (var (key, value) in TestReviewer.Settings)
        {
            settings[key] = value;
        }

        foreach (var (key, value) in overrides)
        {
            settings[key] = value;
        }

        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(RepositoryRoot(), "src", "FiatCryptoGateway.Api", "appsettings.json"), optional: false, reloadOnChange: false)
            .AddInMemoryCollection(settings)
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGatewayApi(configuration);
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IStartupValidator>().Validate();
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FiatCryptoGateway.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
