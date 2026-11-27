using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using FiatCryptoGateway.Api.Contracts;
using FiatCryptoGateway.Application.Ports;
using FiatCryptoGateway.Application.Webhooks;
using FiatCryptoGateway.Sandbox;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace FiatCryptoGateway.Api.Tests;

/// <summary>The compliance console page and the reviewer endpoints added for it.</summary>
public sealed partial class ReviewConsoleTests(GatewayApiFactory factory) : IClassFixture<GatewayApiFactory>
{
    [Fact]
    public async Task ConsolePage_IsLockedDownAndHoldsNoData()
    {
        using var browser = factory.CreateClient();

        using var response = await browser.GetAsync(new Uri("/review", UriKind.Relative));
        var page = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var policy = response.Headers.GetValues("Content-Security-Policy").Single();
        var nonce = NonceInPolicy().Match(policy).Groups[1].Value;
        Assert.NotEmpty(nonce);
        Assert.Contains("default-src 'none'", policy, StringComparison.Ordinal);
        Assert.Contains("form-action 'none'", policy, StringComparison.Ordinal); // no form can send a password anywhere by itself
        Assert.Contains("frame-ancestors 'none'", policy, StringComparison.Ordinal);
        Assert.Contains($"<script nonce=\"{nonce}\">", page, StringComparison.Ordinal);
        Assert.Contains($"<style nonce=\"{nonce}\">", page, StringComparison.Ordinal);
        Assert.DoesNotContain("__NONCE__", page, StringComparison.Ordinal);
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Contains("data-time-zone=\"Asia/Kolkata\"", page, StringComparison.Ordinal);
        Assert.Contains("autocomplete=\"username\"", page, StringComparison.Ordinal);
        Assert.Contains("autocomplete=\"current-password\"", page, StringComparison.Ordinal);
        Assert.Contains("autocomplete=\"one-time-code\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Reviewer key", page, StringComparison.Ordinal);
        Assert.Contains("[hidden] { display: none !important; }", page, StringComparison.Ordinal); // signed-out controls stay hidden
        foreach (var id in new[] { "count-open", "backup-state", "open-list", "backup-list", "email-list" })
        {
            Assert.Contains($"id=\"{id}\"", page, StringComparison.Ordinal); // the backups and alerts view
        }
        Assert.DoesNotMatch("(src|href)=\"(https?:)?//", page); // nothing loaded from other sites
    }

    [Fact]
    public async Task ConsolePage_CanBeTurnedOff()
    {
        using var host = factory.WithWebHostBuilder(builder => builder.UseSetting("Gateway:Review:ConsoleEnabled", "false"));
        using var browser = host.CreateClient();

        using var response = await browser.GetAsync(new Uri("/review", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task FailedNotifications_AreListedForReviewers_AndCanBeSentAgain()
    {
        var sender = new SwitchableSender();
        using var host = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:Webhooks:MaxAttempts", "1");
            builder.UseSetting("Gateway:ApiKeys:Clients:0:WebhookUrl", "https://merchant.example/hooks");
            builder.UseSetting("Gateway:ApiKeys:Clients:0:WebhookSecret", "test-only-merchant-webhook-secret-0123456789");
            builder.ConfigureTestServices(services => services.AddSingleton<IWebhookSender>(sender));
        });
        var (merchantA, merchantB, reviewer) = await ClientsAsync(host);
        var withWebhook = await ApiFlows.CreateOrderAsync(merchantA);
        var withoutWebhook = await ApiFlows.CreateOrderAsync(merchantB); // client-b has no webhook: not a failure
        var dispatcher = host.Services.GetRequiredService<MerchantWebhookDispatcher>();
        while (await dispatcher.DispatchDueAsync(CancellationToken.None) > 0)
        {
        }

        var failed = await reviewer.GetFromJsonAsync<List<FailedDeliveryResponse>>("/v1/review/webhooks/failed");
        var orderFailure = Assert.Single(failed!, delivery => delivery.About == "payment_order");
        Assert.Equal((withWebhook.OrderId, "client-a", "HTTP_500", 1), (orderFailure.AboutId, orderFailure.Merchant, orderFailure.LastError, orderFailure.Attempts));
        Assert.DoesNotContain(failed!, delivery => delivery.AboutId == withoutWebhook.OrderId);
        using var merchantLooks = await merchantA.GetAsync(new Uri("/v1/review/webhooks/failed", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Forbidden, merchantLooks.StatusCode);

        sender.Fail = false; // the merchant fixed its endpoint
        using var retried = await reviewer.PostAsync(new Uri($"/v1/review/webhooks/{orderFailure.EventId}/retry", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.NoContent, retried.StatusCode);
        Assert.DoesNotContain((await reviewer.GetFromJsonAsync<List<FailedDeliveryResponse>>("/v1/review/webhooks/failed"))!, d => d.EventId == orderFailure.EventId);
        await dispatcher.DispatchDueAsync(CancellationToken.None);
        var deliveries = await reviewer.GetFromJsonAsync<List<WebhookDeliveryResponse>>($"/v1/review/orders/{withWebhook.OrderId}/webhooks");
        Assert.Equal("Delivered", Assert.Single(deliveries!).Status);

        using var again = await reviewer.PostAsync(new Uri($"/v1/review/webhooks/{orderFailure.EventId}/retry", UriKind.Relative), null);
        Assert.Equal("DELIVERY_NOT_FAILED", (await again.Content.ReadFromJsonAsync<ErrorResponse>())!.Error.Code);
    }

    [Fact]
    public async Task FrozenPayments_AreListedUntilReleased()
    {
        using var host = factory.WithWebHostBuilder(_ => { });
        var (merchant, _, reviewer) = await ClientsAsync(host);
        var order = await ApiFlows.CreateOrderAsync(merchant, reviewer, SandboxWalletScreening.HighRiskAddress);
        await ApiFlows.PostAsync<OrderResponse>(merchant, $"/v1/sandbox/orders/{order.OrderId}/pay", new { });
        await ApiFlows.PostAsync<ReviewOrderResponse>(reviewer, $"/v1/review/orders/{order.OrderId}/reject", new { reason = "Linked to fraud." });
        await ApiFlows.PostAsync<ReviewOrderResponse>(reviewer, $"/v1/review/orders/{order.OrderId}/freeze", new { reason = "Reported.", reportReference = "STR-31" });

        var frozen = await reviewer.GetFromJsonAsync<List<ReviewQueueItemResponse>>("/v1/review/frozen");
        var item = Assert.Single(frozen!, entry => entry.OrderId == order.OrderId);
        Assert.Equal(("Frozen", "STR-31", false), (item.Status, item.ReportReference, item.Overdue));

        await ApiFlows.PostAsync<ReviewOrderResponse>(reviewer, $"/v1/review/orders/{order.OrderId}/release", new { reason = "Release instructed." });
        Assert.DoesNotContain((await reviewer.GetFromJsonAsync<List<ReviewQueueItemResponse>>("/v1/review/frozen"))!, entry => entry.OrderId == order.OrderId);
    }

    private static async Task<(HttpClient MerchantA, HttpClient MerchantB, HttpClient Reviewer)> ClientsAsync(WebApplicationFactory<Program> host)
    {
        static HttpClient With(WebApplicationFactory<Program> host, string key)
        {
            var client = host.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
            return client;
        }

        return (With(host, GatewayApiFactory.ClientAKey), With(host, GatewayApiFactory.ClientBKey), await host.SignInReviewerAsync());
    }

    [GeneratedRegex("'nonce-([^']+)'")]
    private static partial Regex NonceInPolicy();

    private sealed class SwitchableSender : IWebhookSender
    {
        public bool Fail { get; set; } = true;

        public Task<WebhookDeliveryResult> SendAsync(Uri url, Guid eventId, string body, string signature, CancellationToken cancellationToken) =>
            Task.FromResult(Fail ? WebhookDeliveryResult.Failed("HTTP_500") : WebhookDeliveryResult.Success);
    }
}
