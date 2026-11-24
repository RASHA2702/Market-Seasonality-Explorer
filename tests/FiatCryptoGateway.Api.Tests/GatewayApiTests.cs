using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using FiatCryptoGateway.Api.Contracts;
using FiatCryptoGateway.Application.OnRamp;
using FiatCryptoGateway.Sandbox;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FiatCryptoGateway.Api.Tests;

public sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>The real API host with two sandbox clients. Keys here are test-only values, not secrets.</summary>
public sealed class GatewayApiFactory : WebApplicationFactory<Program>
{
    public const string ClientAKey = "test-only-key-client-a";
    public const string ClientBKey = "test-only-key-client-b";
    public const string ReviewerKey = "test-only-key-reviewer";

    public ManualTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero));

    public HttpClient CreateClientFor(string apiKey)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return client;
    }

    public static string Sha256Hex(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Gateway:ApiKeys:Clients:0:ClientId", "client-a");
        builder.UseSetting("Gateway:ApiKeys:Clients:0:KeySha256", Sha256Hex(ClientAKey));
        builder.UseSetting("Gateway:ApiKeys:Clients:1:ClientId", "client-b");
        builder.UseSetting("Gateway:ApiKeys:Clients:1:KeySha256", Sha256Hex(ClientBKey));
        builder.UseSetting("Gateway:ApiKeys:Clients:2:ClientId", "compliance-officer");
        builder.UseSetting("Gateway:ApiKeys:Clients:2:KeySha256", Sha256Hex(ReviewerKey));
        builder.UseSetting("Gateway:ApiKeys:Clients:2:Role", "Reviewer");
        builder.UseSetting("Gateway:Processing:Enabled", "false");
        builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(Time));
    }
}

public sealed class GatewayApiTests(GatewayApiFactory factory) : IClassFixture<GatewayApiFactory>
{
    private const string Address = "0x1111111111111111111111111111111111111111";

    [Theory]
    [InlineData("Gateway:Mode", "Production")]
    [InlineData("Gateway:AllowRealFundsMovement", "true")]
    public void ProductionSettings_StopStartup(string key, string value)
    {
        using var host = factory.WithWebHostBuilder(builder => builder.UseSetting(key, value));

        var exception = Assert.Throws<OptionsValidationException>(() => host.CreateClient());

        Assert.Contains("sandbox-only", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Gateway:Providers:anypartner:Environment", "Live")]
    [InlineData("Gateway:Providers:anypartner:ApiKey", "sk_live_not_a_real_key")]
    public void LiveProviderSettings_StopStartup(string key, string value)
    {
        using var host = factory.WithWebHostBuilder(builder => builder.UseSetting(key, value));

        Assert.Throws<OptionsValidationException>(() => host.CreateClient());
    }

    [Fact]
    public void MalformedApiKeyHash_StopsStartup()
    {
        using var host = factory.WithWebHostBuilder(builder => builder.UseSetting("Gateway:ApiKeys:Clients:0:KeySha256", "not-a-hash"));

        Assert.Throws<OptionsValidationException>(() => host.CreateClient());
    }

    [Fact]
    public async Task Environment_IsPublicAndReportsSandbox()
    {
        using var client = factory.CreateClient();

        var environment = await client.GetFromJsonAsync<EnvironmentResponse>("/v1/environment");

        Assert.NotNull(environment);
        Assert.Equal("Sandbox", environment.Mode);
        Assert.False(environment.RealFundsMovement);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-key")]
    public async Task MissingOrWrongApiKey_IsUnauthorized(string? key)
    {
        using var client = key is null ? factory.CreateClient() : factory.CreateClientFor(key);

        using var response = await client.GetAsync(new Uri("/v1/assets", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task FullOrder_OverHttp_CompletesWithABalancedLedger()
    {
        using var client = factory.CreateClientFor(GatewayApiFactory.ClientAKey);

        var quote = await CreateQuoteAsync(client);
        Assert.Equal("150.00", quote.ServiceFee);
        Assert.Equal("114.148850", quote.DestinationAmount);

        var order = await CreateOrderAsync(client, quote.QuoteId, Guid.NewGuid().ToString());
        Assert.Equal("AwaitingFiatPayment", order.Status);

        var paid = await PostAsync<OrderResponse>(client, $"/v1/sandbox/orders/{order.OrderId}/pay", new { });
        Assert.Equal("CryptoSent", paid.Status);

        var confirmed = await PostAsync<OrderResponse>(client, $"/v1/sandbox/orders/{order.OrderId}/confirm", new { });
        Assert.Equal("Completed", confirmed.Status);

        var ledger = await client.GetFromJsonAsync<List<LedgerEntryResponse>>($"/v1/orders/{order.OrderId}/ledger");
        Assert.Equal(["FIAT_RECEIVED", "CRYPTO_PURCHASED", "CRYPTO_SENT"], ledger!.Select(entry => entry.Kind));

        using var reviewer = factory.CreateClientFor(GatewayApiFactory.ReviewerKey);
        var balances = await reviewer.GetFromJsonAsync<LedgerBalancesResponse>("/v1/review/ledger/balances");
        Assert.True(balances!.BalancedPerAsset);
    }

    [Fact]
    public async Task MerchantAndReviewerKeys_CannotUseEachOthersEndpoints()
    {
        using var merchant = factory.CreateClientFor(GatewayApiFactory.ClientAKey);
        using var reviewer = factory.CreateClientFor(GatewayApiFactory.ReviewerKey);

        using var merchantOnQueue = await merchant.GetAsync(new Uri("/v1/review/queue", UriKind.Relative));
        using var merchantOnLedger = await merchant.GetAsync(new Uri("/v1/review/ledger/balances", UriKind.Relative));
        using var reviewerOnQuotes = await reviewer.PostAsJsonAsync(
            new Uri("/v1/quotes", UriKind.Relative), new { sourceCurrency = "INR", sourceAmount = "10000.00", destinationAsset = "USDC@ethereum" });

        Assert.Equal(HttpStatusCode.Forbidden, merchantOnQueue.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, merchantOnLedger.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, reviewerOnQuotes.StatusCode);
    }

    [Fact]
    public async Task HeldPayment_IsReviewedWithAReasonOverHttp()
    {
        using var merchant = factory.CreateClientFor(GatewayApiFactory.ClientAKey);
        using var reviewer = factory.CreateClientFor(GatewayApiFactory.ReviewerKey);
        var quote = await CreateQuoteAsync(merchant);
        using var created = await SendOrderAsync(
            merchant, quote.QuoteId, Guid.NewGuid().ToString(), destinationAddress: SandboxWalletScreening.HighRiskAddress);
        var order = (await created.Content.ReadFromJsonAsync<OrderResponse>())!;
        var held = await PostAsync<OrderResponse>(merchant, $"/v1/sandbox/orders/{order.OrderId}/pay", new { });
        Assert.Equal("OnHold", held.Status); // merchants never see review, rejection or freezing as such

        var queue = await reviewer.GetFromJsonAsync<List<ReviewQueueItemResponse>>("/v1/review/queue");
        var item = Assert.Single(queue!, entry => entry.OrderId == order.OrderId);
        Assert.Equal("client-a", item.Merchant);
        Assert.Contains("WALLET_HIGH_RISK", item.Reasons);

        using var noReason = await reviewer.PostAsJsonAsync(new Uri($"/v1/review/orders/{order.OrderId}/approve", UriKind.Relative), new { });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noReason.StatusCode);

        var reviewed = await PostAsync<ReviewOrderResponse>(
            reviewer, $"/v1/review/orders/{order.OrderId}/approve", new { reason = "Wallet owner verified by phone." });
        Assert.Equal("CryptoSent", reviewed.Order.Status);
        var action = Assert.Single(reviewed.ReviewActions);
        Assert.Equal(("Approve", "compliance-officer"), (action.Action, action.Reviewer));
    }

    [Fact]
    public async Task OrderCreation_IsIdempotent()
    {
        using var client = factory.CreateClientFor(GatewayApiFactory.ClientAKey);
        var quote = await CreateQuoteAsync(client);
        var key = Guid.NewGuid().ToString();

        var first = await CreateOrderAsync(client, quote.QuoteId, key);
        using var replay = await SendOrderAsync(client, quote.QuoteId, key);
        var replayed = await replay.Content.ReadFromJsonAsync<OrderResponse>();

        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(first.OrderId, replayed!.OrderId);
        Assert.Equal("true", replay.Headers.GetValues("Idempotent-Replayed").Single());

        using var conflicting = await SendOrderAsync(client, quote.QuoteId, key, customerId: SandboxCustomerDirectory.BasicKyc);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, conflicting.StatusCode);
        Assert.Equal("IDEMPOTENCY_KEY_REUSED", (await conflicting.Content.ReadFromJsonAsync<ErrorResponse>())!.Error.Code);

        using var missingKey = await SendOrderAsync(client, quote.QuoteId, key: null);
        Assert.Equal(HttpStatusCode.BadRequest, missingKey.StatusCode);
    }

    [Fact]
    public async Task RuleViolations_ReturnStableErrorCodes()
    {
        using var client = factory.CreateClientFor(GatewayApiFactory.ClientAKey);

        using var tooSmall = await client.PostAsJsonAsync(
            new Uri("/v1/quotes", UriKind.Relative), new { sourceCurrency = "INR", sourceAmount = "100.00", destinationAsset = "USDC@ethereum" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooSmall.StatusCode);
        Assert.Equal("BELOW_MINIMUM_AMOUNT", (await tooSmall.Content.ReadFromJsonAsync<ErrorResponse>())!.Error.Code);

        var quote = await CreateQuoteAsync(client);
        using var unverified = await SendOrderAsync(client, quote.QuoteId, Guid.NewGuid().ToString(), SandboxCustomerDirectory.Unverified);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unverified.StatusCode);
        Assert.Equal("KYC_REQUIRED", (await unverified.Content.ReadFromJsonAsync<ErrorResponse>())!.Error.Code);
    }

    [Fact]
    public async Task Clients_CannotSeeEachOthersOrders()
    {
        using var clientA = factory.CreateClientFor(GatewayApiFactory.ClientAKey);
        using var clientB = factory.CreateClientFor(GatewayApiFactory.ClientBKey);
        var order = await CreateOrderAsync(clientA, (await CreateQuoteAsync(clientA)).QuoteId, Guid.NewGuid().ToString());

        using var response = await clientB.GetAsync(new Uri($"/v1/orders/{order.OrderId}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PaymentWebhook_VerifiesSignatureAndIgnoresRedelivery()
    {
        using var client = factory.CreateClientFor(GatewayApiFactory.ClientAKey);
        var order = await CreateOrderAsync(client, (await CreateQuoteAsync(client)).QuoteId, Guid.NewGuid().ToString());
        var onRamp = factory.Services.GetRequiredService<OnRampService>();
        var handler = factory.Services.GetRequiredService<SandboxFiatWebhookHandler>();
        var (body, signature) = handler.CreateSignedPaymentEvent(await onRamp.GetOrderAsync("client-a", order.OrderId, CancellationToken.None));

        using var forged = await PostWebhookAsync(client, body, signature.Replace("v1=", "v1=0", StringComparison.Ordinal)[..^1]);
        Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);

        using var first = await PostWebhookAsync(client, body, signature);
        using var second = await PostWebhookAsync(client, body, signature);
        Assert.Equal("accepted", (await first.Content.ReadFromJsonAsync<WebhookAcknowledgement>())!.Status);
        Assert.Equal("duplicate", (await second.Content.ReadFromJsonAsync<WebhookAcknowledgement>())!.Status);

        var ledger = await client.GetFromJsonAsync<List<LedgerEntryResponse>>($"/v1/orders/{order.OrderId}/ledger");
        Assert.Single(ledger!, entry => entry.Kind == "FIAT_RECEIVED");
    }

    [Fact]
    public async Task FrozenPayment_LooksOnlyOnHoldToTheMerchant()
    {
        using var merchant = factory.CreateClientFor(GatewayApiFactory.ClientAKey);
        using var reviewer = factory.CreateClientFor(GatewayApiFactory.ReviewerKey);
        var quote = await CreateQuoteAsync(merchant);
        using var created = await SendOrderAsync(
            merchant, quote.QuoteId, Guid.NewGuid().ToString(), destinationAddress: SandboxWalletScreening.HighRiskAddress);
        var order = (await created.Content.ReadFromJsonAsync<OrderResponse>())!;
        await PostAsync<OrderResponse>(merchant, $"/v1/sandbox/orders/{order.OrderId}/pay", new { });
        await PostAsync<ReviewOrderResponse>(reviewer, $"/v1/review/orders/{order.OrderId}/reject", new { reason = "Linked to fraud." });
        await PostAsync<ReviewOrderResponse>(
            reviewer, $"/v1/review/orders/{order.OrderId}/freeze", new { reason = "Reported.", reportReference = "STR-77" });

        var merchantBody = await merchant.GetStringAsync(new Uri($"/v1/orders/{order.OrderId}", UriKind.Relative));
        var merchantView = await merchant.GetFromJsonAsync<OrderResponse>($"/v1/orders/{order.OrderId}");

        Assert.Equal("OnHold", merchantView!.Status);
        Assert.Single(merchantView.Events, e => e.To == "OnHold");
        foreach (var word in new[] { "Frozen", "FROZEN", "REJECT", "Reject", "STR-77", "REPORT" })
        {
            Assert.DoesNotContain(word, merchantBody, StringComparison.Ordinal);
        }

        var reviewerView = await reviewer.GetFromJsonAsync<ReviewOrderResponse>($"/v1/review/orders/{order.OrderId}");
        Assert.Equal("Frozen", reviewerView!.Order.Status);
        Assert.Equal("STR-77", reviewerView.ReportReference);
    }

    private static async Task<QuoteResponse> CreateQuoteAsync(HttpClient client) =>
        await PostAsync<QuoteResponse>(
            client, "/v1/quotes", new { sourceCurrency = "INR", sourceAmount = "10000.00", destinationAsset = "USDC@ethereum" }, HttpStatusCode.Created);

    private static async Task<OrderResponse> CreateOrderAsync(HttpClient client, Guid quoteId, string key)
    {
        using var response = await SendOrderAsync(client, quoteId, key);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<OrderResponse>())!;
    }

    private static Task<HttpResponseMessage> SendOrderAsync(
        HttpClient client,
        Guid quoteId,
        string? key,
        string customerId = SandboxCustomerDirectory.Verified,
        string destinationAddress = Address)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/v1/orders", UriKind.Relative))
        {
            Content = JsonContent.Create(new { quoteId, customerId, destinationAddress }),
        };
        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> PostWebhookAsync(HttpClient client, string body, string signature)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/v1/webhooks/sandbox-fiat", UriKind.Relative))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(SandboxFiatWebhookHandler.SignatureHeader, signature);
        return client.SendAsync(request);
    }

    private static async Task<T> PostAsync<T>(HttpClient client, string path, object body, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var response = await client.PostAsJsonAsync(new Uri(path, UriKind.Relative), body);
        Assert.Equal(expected, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
}
