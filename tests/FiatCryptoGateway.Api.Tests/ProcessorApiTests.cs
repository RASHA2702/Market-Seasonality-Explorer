using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using FiatCryptoGateway.Api.Contracts;
using FiatCryptoGateway.Application.Payments;
using FiatCryptoGateway.Application.Ports;
using FiatCryptoGateway.Domain.Payments;
using FiatCryptoGateway.Sandbox;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FiatCryptoGateway.Api.Tests;

/// <summary>The API with the gateway's own processor collecting the money, as it does everywhere outside these tests.</summary>
public sealed class ProcessorApiTests(GatewayApiFactory factory) : IClassFixture<GatewayApiFactory>
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Theory]
    [InlineData("Processor", "gateway-processor")]
    [InlineData("SandboxPartner", "sandbox-fiat")]
    public async Task TheEnvironment_NamesTheProviderThatCollectsTheMoney(string choice, string name)
    {
        using var host = Host(choice);
        using var client = host.CreateClient();

        var environment = await client.GetFromJsonAsync<EnvironmentResponse>(new Uri("/v1/environment", UriKind.Relative));

        Assert.Equal(name, environment!.FiatProvider);
        Assert.False(environment.RealFundsMovement);
    }

    [Fact]
    public async Task TheSandboxPayEndpoint_PaysThroughTheProcessor()
    {
        using var host = Host("Processor");
        using var merchant = Merchant(host);
        var order = await ApiFlows.CreateOrderAsync(merchant);

        using var paid = await merchant.PostAsJsonAsync(new Uri($"/v1/sandbox/orders/{order.OrderId}/pay", UriKind.Relative), new { });

        Assert.Equal(HttpStatusCode.OK, paid.StatusCode);
        Assert.Equal("CryptoSent", (await paid.Content.ReadFromJsonAsync<OrderResponse>())!.Status);
        var attempt = Assert.Single(await Attempts(host).ListForOrderAsync(order.OrderId, None));
        Assert.Equal((PaymentAttemptStatus.Captured, PaymentMethod.Upi, SandboxTestInstruments.SuccessfulUpiAddress), (attempt.Status, attempt.Method, attempt.Instrument.Label));
        Assert.NotNull(attempt.RecordedOnOrderAt);
        var ledger = await merchant.GetFromJsonAsync<List<LedgerEntryResponse>>(new Uri($"/v1/orders/{order.OrderId}/ledger", UriKind.Relative));
        Assert.Single(ledger!, entry => entry.Kind == "FIAT_RECEIVED");
    }

    [Theory]
    [InlineData("""{ "amount": "5.00" }""")]
    [InlineData("""{ "lateByMinutes": 1 }""")]
    public async Task TheSandboxPayEndpoint_CannotMakeAWrongOrLatePaymentWithTheProcessor(string body)
    {
        using var host = Host("Processor");
        using var merchant = Merchant(host);
        var order = await ApiFlows.CreateOrderAsync(merchant);

        using var response = await merchant.PostAsync(
            new Uri($"/v1/sandbox/orders/{order.OrderId}/pay", UriKind.Relative), new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("NOT_SUPPORTED_WITH_PROCESSOR", (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Error.Code);
        Assert.Empty(await Attempts(host).ListForOrderAsync(order.OrderId, None));
    }

    [Fact]
    public async Task TheCheckoutPageShortcut_PaysThroughTheProcessor_AndPaysOnce()
    {
        using var host = Host("Processor");
        using var merchant = Merchant(host);
        var order = await ApiFlows.CreateOrderAsync(merchant);
        using var browser = Browser(host);

        using var first = await PayWithShortcutAsync(browser, order);
        using var second = await PayWithShortcutAsync(browser, order); // a double submission pays once

        Assert.Equal((HttpStatusCode.SeeOther, HttpStatusCode.SeeOther), (first.StatusCode, second.StatusCode));
        Assert.Contains("Payment received", await browser.GetStringAsync(order.CheckoutUrl!.AbsolutePath), StringComparison.Ordinal);
        Assert.Single(await Attempts(host).ListForOrderAsync(order.OrderId, None), attempt => attempt.WasCaptured);
        var ledger = await merchant.GetFromJsonAsync<List<LedgerEntryResponse>>(new Uri($"/v1/orders/{order.OrderId}/ledger", UriKind.Relative));
        Assert.Single(ledger!, entry => entry.Kind == "FIAT_RECEIVED");
    }

    [Fact]
    public async Task ARejectedPayment_IsRefundedThroughTheProcessor_AndTheCustomerSeesIt()
    {
        using var host = Host("Processor");
        using var merchant = Merchant(host);
        using var reviewer = await host.SignInReviewerAsync();
        var order = await ApiFlows.CreateOrderAsync(merchant, reviewer, SandboxWalletScreening.HighRiskAddress);
        using var browser = Browser(host);
        using var paid = await PayWithShortcutAsync(browser, order);

        await ApiFlows.PostAsync<ReviewOrderResponse>(reviewer, $"/v1/review/orders/{order.OrderId}/reject", new { reason = "Linked to fraud." });
        await ApiFlows.PostAsync<ReviewOrderResponse>(reviewer, $"/v1/review/orders/{order.OrderId}/freeze", new { reason = "Reported.", reportReference = "STR-91" });
        await ApiFlows.PostAsync<ReviewOrderResponse>(reviewer, $"/v1/review/orders/{order.OrderId}/release", new { reason = "Release instructed." });

        Assert.Contains("Payment refunded", await browser.GetStringAsync(order.CheckoutUrl!.AbsolutePath), StringComparison.Ordinal);
        var attempt = Assert.Single(await Attempts(host).ListForOrderAsync(order.OrderId, None));
        Assert.Equal(PaymentAttemptStatus.Refunded, attempt.Status);
    }

    [Fact]
    public async Task TheBackgroundWorker_EndsAttemptsThatRanOutOfTime()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 25, 6, 0, 0, TimeSpan.Zero));
        using var host = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:Fiat:Provider", "Processor");
            builder.UseSetting("Gateway:Processing:Enabled", "true");
            builder.UseSetting("Gateway:Processing:IntervalSeconds", "1");
            builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(clock));
        });
        using var merchant = Merchant(host);
        var order = await ApiFlows.CreateOrderAsync(merchant);
        var waiting = await host.Services.GetRequiredService<PaymentProcessor>()
            .StartAsync(order.OrderId, new CardInput("4242 4242 4242 4242", 12, 2028, "737", holderName: null), None);
        clock.Advance(TimeSpan.FromMinutes(11)); // past the attempt's ten minutes, inside the order's fifteen

        var ended = false;
        for (var waited = 0; waited < 100 && !ended; waited++)
        {
            ended = (await Attempts(host).FindAsync(waiting.AttemptId, None))!.Status == PaymentAttemptStatus.Expired;
            if (!ended)
            {
                await Task.Delay(100, None);
            }
        }

        Assert.True(ended, "The background worker did not end the attempt that ran out of time.");
    }

    [Fact]
    public void TheWorker_IsPartOfTheHost()
    {
        using var host = Host("Processor");

        Assert.Single(host.Services.GetServices<IHostedService>().OfType<PaymentProcessingWorker>());
    }

    private WebApplicationFactory<Program> Host(string choice) =>
        factory.WithWebHostBuilder(builder => builder.UseSetting("Gateway:Fiat:Provider", choice));

    private static IPaymentAttemptStore Attempts(WebApplicationFactory<Program> host) => host.Services.GetRequiredService<IPaymentAttemptStore>();

    private static HttpClient Merchant(WebApplicationFactory<Program> host)
    {
        var merchant = host.CreateClient();
        merchant.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GatewayApiFactory.ClientAKey);
        return merchant;
    }

    /// <summary>A customer's browser: no API key, and redirects are seen rather than followed.</summary>
    private static HttpClient Browser(WebApplicationFactory<Program> host) =>
        host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static Task<HttpResponseMessage> PayWithShortcutAsync(HttpClient browser, OrderResponse order) =>
        browser.PostAsync(new Uri(order.CheckoutUrl!.AbsolutePath + "/sandbox-pay", UriKind.Relative), new FormUrlEncodedContent([]));
}
