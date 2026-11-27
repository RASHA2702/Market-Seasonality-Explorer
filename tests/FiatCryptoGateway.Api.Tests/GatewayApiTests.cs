using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FiatCryptoGateway.Api.Contracts;
using FiatCryptoGateway.Application.Checkout;
using FiatCryptoGateway.Application.OnRamp;
using FiatCryptoGateway.Application.Ports;
using FiatCryptoGateway.Application.Webhooks;
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

/// <summary>The real API host with two sandbox merchants and a reviewer. Keys here are test-only values, not secrets.</summary>
public sealed class GatewayApiFactory : WebApplicationFactory<Program>
{
    public const string ClientAKey = "test-only-key-client-a";
    public const string ClientBKey = "test-only-key-client-b";

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
        foreach (var (key, value) in TestReviewer.Settings)
        {
            builder.UseSetting(key, value);
        }

        builder.UseSetting("Gateway:Fiat:Provider", "SandboxPartner"); // most tests pay by the simulated partner's webhook
        builder.UseSetting("Gateway:Processing:Enabled", "false");
        builder.UseSetting("Gateway:Webhooks:Enabled", "false"); // tests send notifications by calling the dispatcher
        builder.UseSetting("Gateway:Reconciliation:Enabled", "false"); // tests start runs through the API
        builder.UseSetting("Gateway:Settlement:CoolingOffHours", "0"); // wallets usable at once; one test checks the real period
        builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(Time));
    }
}

/// <summary>Captures notifications instead of posting them, and answers each with success.</summary>
public sealed class RecordingWebhookSender : IWebhookSender
{
    private readonly List<(Uri Url, Guid EventId, string Body, string Signature)> _sent = [];

    public IReadOnlyList<(Uri Url, Guid EventId, string Body, string Signature)> Sent
    {
        get
        {
            lock (_sent)
            {
                return [.. _sent];
            }
        }
    }

    public Task<WebhookDeliveryResult> SendAsync(Uri url, Guid eventId, string body, string signature, CancellationToken cancellationToken)
    {
        lock (_sent)
        {
            _sent.Add((url, eventId, body, signature));
        }

        return Task.FromResult(WebhookDeliveryResult.Success);
    }
}

public sealed class GatewayApiTests(GatewayApiFactory factory) : IClassFixture<GatewayApiFactory>
{
    private const string Address = "0x1111111111111111111111111111111111111111";
    private const string WebhookSecret = "test-only-merchant-webhook-secret-0123456789";
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task MerchantWebhooks_AreSignedOrderedAndRevealNoComplianceDetail()
    {
        var sender = new RecordingWebhookSender();
        using var host = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:ApiKeys:Clients:0:WebhookUrl", "https://merchant.example/hooks");
            builder.UseSetting("Gateway:ApiKeys:Clients:0:WebhookSecret", WebhookSecret);
            builder.ConfigureTestServices(services => services.AddSingleton<IWebhookSender>(sender));
        });
        using var merchant = host.CreateClient();
        merchant.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GatewayApiFactory.ClientAKey);
        using var reviewer = await host.SignInReviewerAsync();
        var quote = await CreateQuoteAsync(merchant);
        using var created = await SendOrderAsync(
            merchant, quote.QuoteId, Guid.NewGuid().ToString(), wallet: SandboxWalletScreening.HighRiskAddress, reviewer: reviewer);
        var order = (await created.Content.ReadFromJsonAsync<OrderResponse>())!;
        await PostAsync<OrderResponse>(merchant, $"/v1/sandbox/orders/{order.OrderId}/pay", new { });
        await PostAsync<ReviewOrderResponse>(reviewer, $"/v1/review/orders/{order.OrderId}/reject", new { reason = "Linked to fraud." });
        await PostAsync<ReviewOrderResponse>(
            reviewer, $"/v1/review/orders/{order.OrderId}/freeze", new { reason = "Reported.", reportReference = "STR-91" });
        await PostAsync<ReviewOrderResponse>(reviewer, $"/v1/review/orders/{order.OrderId}/release", new { reason = "Release instructed." });

        var dispatcher = host.Services.GetRequiredService<MerchantWebhookDispatcher>();
        while (await dispatcher.DispatchDueAsync(CancellationToken.None) > 0)
        {
        }

        var now = factory.Time.GetUtcNow();
        var sent = sender.Sent;
        Assert.All(sent, delivery =>
        {
            Assert.Equal(new Uri("https://merchant.example/hooks"), delivery.Url);
            Assert.True(WebhookSignature.Verify(WebhookSecret, delivery.Signature, delivery.Body, now, TimeSpan.FromMinutes(5)));
            foreach (var word in new[] { "Frozen", "FROZEN", "REJECT", "Reject", "STR-91", "REPORT", "Compliance", "fraud" })
            {
                Assert.DoesNotContain(word, delivery.Body, StringComparison.Ordinal);
            }
        });
        Assert.All(sent, delivery => Assert.Equal(delivery.EventId, JsonDocument.Parse(delivery.Body).RootElement.GetProperty("id").GetGuid()));
        // The order's notifications; registering its wallet sent wallet notifications too.
        var notifications = sent
            .Select(delivery => JsonDocument.Parse(delivery.Body).RootElement)
            .Where(json => json.GetProperty("type").GetString() == "order.status_changed")
            .ToList();
        var data = notifications.Select(json => json.GetProperty("data")).ToList();
        Assert.Equal(
            ["AwaitingFiatPayment", "FiatReceived", "OnHold", "RefundPending", "Refunded"],
            data.Select(item => item.GetProperty("status").GetString()));
        Assert.Equal([1, 2, 3, 4, 5], data.Select(item => item.GetProperty("sequence").GetInt32()));

        // The merchant's order view and its notifications number the same events the same way.
        var merchantView = await merchant.GetFromJsonAsync<OrderResponse>($"/v1/orders/{order.OrderId}");
        Assert.Equal(merchantView!.Events.Select(e => (e.Sequence, e.To)), data.Select(item => (item.GetProperty("sequence").GetInt32(), item.GetProperty("status").GetString()!)));

        var deliveries = await reviewer.GetFromJsonAsync<List<WebhookDeliveryResponse>>($"/v1/review/orders/{order.OrderId}/webhooks");
        Assert.Equal(5, deliveries!.Count);
        Assert.All(deliveries, delivery => Assert.Equal(("Delivered", 1), (delivery.Status, delivery.Attempts)));
        using var merchantOnDeliveries = await merchant.GetAsync(new Uri($"/v1/review/orders/{order.OrderId}/webhooks", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Forbidden, merchantOnDeliveries.StatusCode);
    }

    [Fact]
    public async Task Reconciliation_ReviewerRunsAFinishedDayAndSeesWhatDiffers()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero)); // 17:30 IST
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(clock)));
        using var merchant = host.CreateClient();
        merchant.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GatewayApiFactory.ClientAKey);
        using var reviewer = await host.SignInReviewerAsync();
        var paid = await CreateOrderAsync(merchant, (await CreateQuoteAsync(merchant)).QuoteId, Guid.NewGuid().ToString());
        await PostAsync<OrderResponse>(merchant, $"/v1/sandbox/orders/{paid.OrderId}/pay", new { });
        var lost = await CreateOrderAsync(merchant, (await CreateQuoteAsync(merchant)).QuoteId, Guid.NewGuid().ToString());
        var lostOrder = await host.Services.GetRequiredService<OnRampService>().GetOrderAsync("client-a", lost.OrderId, CancellationToken.None);
        host.Services.GetRequiredService<SandboxFiatWebhookHandler>().CreateSignedPaymentEvent(lostOrder); // its webhook never arrives

        using var tooEarly = await reviewer.PostAsJsonAsync(new Uri("/v1/review/reconciliation/runs", UriKind.Relative), new { day = "2026-11-24" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooEarly.StatusCode);
        Assert.Equal("DAY_NOT_FINISHED", (await tooEarly.Content.ReadFromJsonAsync<ErrorResponse>())!.Error.Code);

        clock.Advance(TimeSpan.FromHours(13)); // 06:30 IST the next day: a new session
        using var nextMorning = await host.SignInReviewerAsync();
        var run = Assert.Single(await PostAsync<List<ReconciliationRunResponse>>(nextMorning, "/v1/review/reconciliation/runs", new { day = "2026-11-24" }));

        Assert.Equal(("sandbox-fiat", "2026-11-24", 2, 1, false), (run.Provider, run.Day, run.ReportLines, run.Matched, run.Clean));
        var discrepancy = Assert.Single(run.Discrepancies);
        Assert.Equal(
            ("MISSING_IN_LEDGER", "FIAT_RECEIVED", (Guid?)lost.OrderId, "10000.00", "INR", (string?)null),
            (discrepancy.Type, discrepancy.Kind, discrepancy.OrderId, discrepancy.ReportAmount, discrepancy.ReportAsset, discrepancy.LedgerAmount));
        var listed = await nextMorning.GetFromJsonAsync<List<ReconciliationRunResponse>>("/v1/review/reconciliation/runs?day=2026-11-24");
        Assert.Equal(run.RunId, Assert.Single(listed!).RunId);

        using var badDay = await nextMorning.PostAsJsonAsync(new Uri("/v1/review/reconciliation/runs", UriKind.Relative), new { day = "24/11/2026" });
        Assert.Equal("INVALID_DAY", (await badDay.Content.ReadFromJsonAsync<ErrorResponse>())!.Error.Code);
        using var merchantRun = await merchant.PostAsJsonAsync(new Uri("/v1/review/reconciliation/runs", UriKind.Relative), new { day = "2026-11-24" });
        Assert.Equal(HttpStatusCode.Forbidden, merchantRun.StatusCode);
    }

    [Fact]
    public async Task SettlementWallets_MerchantRegistersAndTheReviewerDecidesTheRiskyOnes()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 25, 6, 0, 0, TimeSpan.Zero));
        using var host = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:Settlement:CoolingOffHours", "24"); // the real default
            builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(clock));
        });
        using var merchant = host.CreateClient();
        merchant.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GatewayApiFactory.ClientAKey);
        using var otherMerchant = host.CreateClient();
        otherMerchant.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GatewayApiFactory.ClientBKey);
        using var reviewer = await host.SignInReviewerAsync();

        var clean = await PostAsync<SettlementWalletResponse>(
            merchant, "/v1/settlement/wallets", new { network = "polygon", address = Address, label = "Treasury" }, HttpStatusCode.Created);
        Assert.Equal(("polygon", "CoolingOff", clock.GetUtcNow().AddHours(24)), (clean.Network, clean.Status, clean.UsableFrom));
        var again = await PostAsync<SettlementWalletResponse>(merchant, "/v1/settlement/wallets", new { network = "polygon", address = Address });
        Assert.Equal(clean.WalletId, again.WalletId); // 200: already registered

        using var riskyResponse = await merchant.PostAsJsonAsync(
            new Uri("/v1/settlement/wallets", UriKind.Relative), new { network = "ethereum", address = SandboxWalletScreening.HighRiskAddress });
        var riskyBody = await riskyResponse.Content.ReadAsStringAsync();
        var risky = JsonSerializer.Deserialize<SettlementWalletResponse>(riskyBody, WebJson)!;
        Assert.Equal("UnderReview", risky.Status);
        foreach (var word in new[] { "High", "risk", "Risk", "screen" })
        {
            Assert.DoesNotContain(word, riskyBody, StringComparison.Ordinal); // the merchant learns nothing about the screening
        }

        var queue = await reviewer.GetFromJsonAsync<List<ReviewSettlementWalletResponse>>("/v1/review/settlement-wallets");
        var item = Assert.Single(queue!);
        Assert.Equal((risky.WalletId, "client-a", "High", "UnderReview"), (item.WalletId, item.Merchant, item.ScreeningRisk, item.MerchantSees));
        using var noReason = await reviewer.PostAsJsonAsync(new Uri($"/v1/review/settlement-wallets/{risky.WalletId}/approve", UriKind.Relative), new { });
        Assert.Equal("REASON_REQUIRED", (await noReason.Content.ReadFromJsonAsync<ErrorResponse>())!.Error.Code);
        var approved = await PostAsync<ReviewSettlementWalletResponse>(
            reviewer, $"/v1/review/settlement-wallets/{risky.WalletId}/approve", new { reason = "Merchant's own exchange account, verified." });
        Assert.Equal(("Approved", "CoolingOff", "compliance-officer"), (approved.Status, approved.MerchantSees, approved.History[^1].Actor));

        clock.Advance(TimeSpan.FromHours(24));
        var wallets = await merchant.GetFromJsonAsync<List<SettlementWalletResponse>>("/v1/settlement/wallets");
        Assert.Equal([("ethereum", "Active"), ("polygon", "Active")], wallets!.Select(w => (w.Network, w.Status)).Order());

        using var otherDisable = await otherMerchant.PostAsync(new Uri($"/v1/settlement/wallets/{clean.WalletId}/disable", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.NotFound, otherDisable.StatusCode);
        Assert.Empty((await otherMerchant.GetFromJsonAsync<List<SettlementWalletResponse>>("/v1/settlement/wallets"))!);
        var disabled = await PostAsync<SettlementWalletResponse>(merchant, $"/v1/settlement/wallets/{clean.WalletId}/disable", new { });
        Assert.Equal("Disabled", disabled.Status);

        using var badAddress = await merchant.PostAsJsonAsync(new Uri("/v1/settlement/wallets", UriKind.Relative), new { network = "tron", address = Address });
        Assert.Equal("INVALID_WALLET_ADDRESS", (await badAddress.Content.ReadFromJsonAsync<ErrorResponse>())!.Error.Code);
        using var badNetwork = await merchant.PostAsJsonAsync(new Uri("/v1/settlement/wallets", UriKind.Relative), new { network = "bitcoin", address = Address });
        Assert.Equal("UNSUPPORTED_NETWORK", (await badNetwork.Content.ReadFromJsonAsync<ErrorResponse>())!.Error.Code);
        using var merchantOnQueue = await merchant.GetAsync(new Uri("/v1/review/settlement-wallets", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Forbidden, merchantOnQueue.StatusCode);
    }

    [Fact]
    public async Task WalletChanges_AreNotifiedToTheMerchantSignedAndWithoutScreeningDetail()
    {
        var sender = new RecordingWebhookSender();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 25, 6, 0, 0, TimeSpan.Zero));
        using var host = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:Settlement:CoolingOffHours", "24");
            builder.UseSetting("Gateway:ApiKeys:Clients:0:WebhookUrl", "https://merchant.example/hooks");
            builder.UseSetting("Gateway:ApiKeys:Clients:0:WebhookSecret", WebhookSecret);
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<TimeProvider>(clock);
                services.AddSingleton<IWebhookSender>(sender);
            });
        });
        using var merchant = host.CreateClient();
        merchant.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GatewayApiFactory.ClientAKey);
        using var reviewer = await host.SignInReviewerAsync();
        var clean = await PostAsync<SettlementWalletResponse>(merchant, "/v1/settlement/wallets", new { network = "polygon", address = Address }, HttpStatusCode.Created);
        var risky = await PostAsync<SettlementWalletResponse>(
            merchant, "/v1/settlement/wallets", new { network = "ethereum", address = SandboxWalletScreening.HighRiskAddress }, HttpStatusCode.Created);
        await PostAsync<ReviewSettlementWalletResponse>(
            reviewer, $"/v1/review/settlement-wallets/{risky.WalletId}/approve", new { reason = "Verified the owner's identity documents." });

        var dispatcher = host.Services.GetRequiredService<MerchantWebhookDispatcher>();
        while (await dispatcher.DispatchDueAsync(CancellationToken.None) > 0)
        {
        }

        var now = clock.GetUtcNow();
        var delivered = sender.Sent
            .Select(delivery => (delivery, json: JsonDocument.Parse(delivery.Body).RootElement))
            .Where(item => item.json.GetProperty("type").GetString() == "settlement_wallet.status_changed")
            .ToList();
        Assert.Equal(
            new[] { (clean.WalletId, "CoolingOff"), (risky.WalletId, "UnderReview"), (risky.WalletId, "CoolingOff") }.Order(),
            delivered.Select(item => (item.json.GetProperty("data").GetProperty("walletId").GetGuid(), item.json.GetProperty("data").GetProperty("status").GetString()!))
                .Order());
        // One wallet's notifications arrive in the order they happened.
        Assert.Equal(
            ["UnderReview", "CoolingOff"],
            delivered.Where(item => item.json.GetProperty("data").GetProperty("walletId").GetGuid() == risky.WalletId)
                .Select(item => item.json.GetProperty("data").GetProperty("status").GetString()));
        Assert.All(delivered, item =>
        {
            Assert.True(WebhookSignature.Verify(WebhookSecret, item.delivery.Signature, item.delivery.Body, now, TimeSpan.FromMinutes(5)));
            Assert.Equal(item.delivery.EventId, item.json.GetProperty("id").GetGuid());
            foreach (var word in new[] { "High", "risk", "screen", "officer", "identity" })
            {
                Assert.DoesNotContain(word, item.delivery.Body, StringComparison.Ordinal);
            }
        });
        var deliveries = await reviewer.GetFromJsonAsync<List<WebhookDeliveryResponse>>($"/v1/review/settlement-wallets/{risky.WalletId}/webhooks");
        Assert.Equal([("Delivered", 1), ("Delivered", 2)], deliveries!.Select(d => (d.Status, d.OrderEventSequence)));
    }

    [Fact]
    public async Task OrderResponse_LinksToTheHostedCheckoutPage()
    {
        using var host = factory.WithWebHostBuilder(builder => builder.UseSetting("Gateway:Checkout:BaseUrl", "https://pay.example.com"));
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GatewayApiFactory.ClientAKey);

        var order = await CreateOrderAsync(client, (await CreateQuoteAsync(client)).QuoteId, Guid.NewGuid().ToString());

        Assert.StartsWith("https://pay.example.com/checkout/", order.CheckoutUrl!.ToString(), StringComparison.Ordinal);
        Assert.True(host.Services.GetRequiredService<CheckoutLinks>().TryRead(order.CheckoutUrl.Segments[^1], out var orderId));
        Assert.Equal(order.OrderId, orderId);
    }

    [Fact]
    public async Task OrderNamingItsOwnDestination_IsRefused()
    {
        using var client = factory.CreateClientFor(GatewayApiFactory.ClientAKey);
        var quote = await CreateQuoteAsync(client);
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/v1/orders", UriKind.Relative))
        {
            Content = JsonContent.Create(new { quoteId = quote.QuoteId, customerId = SandboxCustomerDirectory.Verified, destinationAddress = Address }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("DESTINATION_NOT_ACCEPTED", (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Error.Code);
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

        using var reviewer = await factory.SignInReviewerAsync();
        var balances = await reviewer.GetFromJsonAsync<LedgerBalancesResponse>("/v1/review/ledger/balances");
        Assert.True(balances!.BalancedPerAsset);
    }

    [Fact]
    public async Task MerchantKeysAndReviewerSessions_CannotUseEachOthersEndpoints()
    {
        using var merchant = factory.CreateClientFor(GatewayApiFactory.ClientAKey);
        using var reviewer = await factory.SignInReviewerAsync();

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
        using var reviewer = await factory.SignInReviewerAsync();
        var quote = await CreateQuoteAsync(merchant);
        using var created = await SendOrderAsync(
            merchant, quote.QuoteId, Guid.NewGuid().ToString(), wallet: SandboxWalletScreening.HighRiskAddress, reviewer: reviewer);
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
        using var reviewer = await factory.SignInReviewerAsync();
        var quote = await CreateQuoteAsync(merchant);
        using var created = await SendOrderAsync(
            merchant, quote.QuoteId, Guid.NewGuid().ToString(), wallet: SandboxWalletScreening.HighRiskAddress, reviewer: reviewer);
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

    /// <summary>Makes <paramref name="wallet"/> the merchant's active Ethereum wallet, then posts the order.</summary>
    private static async Task<HttpResponseMessage> SendOrderAsync(
        HttpClient client,
        Guid quoteId,
        string? key,
        string customerId = SandboxCustomerDirectory.Verified,
        string wallet = Address,
        HttpClient? reviewer = null)
    {
        await UseWalletAsync(client, wallet, reviewer);
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/v1/orders", UriKind.Relative))
        {
            Content = JsonContent.Create(new { quoteId, customerId }),
        };
        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        return await client.SendAsync(request);
    }

    /// <summary>Registers the wallet (a no-op when it is already active) and, if screening held it, has a reviewer approve it.</summary>
    private static async Task UseWalletAsync(HttpClient merchant, string address, HttpClient? reviewer)
    {
        using var response = await merchant.PostAsJsonAsync(new Uri("/v1/settlement/wallets", UriKind.Relative), new { network = "ethereum", address });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var wallet = (await response.Content.ReadFromJsonAsync<SettlementWalletResponse>())!;
        if (wallet.Status == "UnderReview")
        {
            Assert.NotNull(reviewer);
            await PostAsync<ReviewSettlementWalletResponse>(
                reviewer, $"/v1/review/settlement-wallets/{wallet.WalletId}/approve", new { reason = "Test wallet, approved for this scenario." });
        }
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
