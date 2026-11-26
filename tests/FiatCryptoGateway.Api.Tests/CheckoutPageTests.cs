using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using FiatCryptoGateway.Api.Checkout;
using FiatCryptoGateway.Api.Contracts;
using FiatCryptoGateway.Domain.Assets;
using FiatCryptoGateway.Sandbox;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace FiatCryptoGateway.Api.Tests;

/// <summary>The hosted page customers pay on, driven over HTTP as a browser would.</summary>
public sealed partial class CheckoutPageTests(GatewayApiFactory factory) : IClassFixture<GatewayApiFactory>
{
    private const string Address = "0x1111111111111111111111111111111111111111";

    [Fact]
    public async Task Customer_SeesTheAmountMerchantAndDeadline_ThenPaysInTheSandbox()
    {
        using var host = Host(displayName: "Acme Books", returnUrl: "https://merchant.example/return?from=checkout");
        var merchant = Merchant(host);
        var order = await CreateOrderAsync(merchant);
        using var browser = Browser(host);

        var page = await browser.GetStringAsync(order.CheckoutUrl!.AbsolutePath);
        Assert.Contains("Acme Books", page, StringComparison.Ordinal);
        Assert.Contains("₹10,000.00", page, StringComparison.Ordinal);
        Assert.Contains("Sandbox: no real money moves.", page, StringComparison.Ordinal);
        Assert.Contains("IST", page, StringComparison.Ordinal);
        Assert.Contains($"action=\"{order.CheckoutUrl.Segments[^1]}/pay\"", page, StringComparison.Ordinal);

        using var paid = await PayAsync(browser, order);
        Assert.Equal(HttpStatusCode.SeeOther, paid.StatusCode);
        Assert.Equal(order.CheckoutUrl, paid.Headers.Location);
        using var again = await PayAsync(browser, order); // a double submission pays once
        Assert.Equal(HttpStatusCode.SeeOther, again.StatusCode);

        page = await browser.GetStringAsync(order.CheckoutUrl.AbsolutePath);
        Assert.Contains("Payment received", page, StringComparison.Ordinal);
        Assert.Contains($"href=\"https://merchant.example/return?from=checkout&amp;order={order.OrderId}\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("<form", page, StringComparison.Ordinal);
        var ledger = await merchant.GetFromJsonAsync<List<LedgerEntryResponse>>($"/v1/orders/{order.OrderId}/ledger");
        Assert.Single(ledger!, entry => entry.Kind == "FIAT_RECEIVED");
        Assert.Equal("CryptoSent", (await merchant.GetFromJsonAsync<OrderResponse>($"/v1/orders/{order.OrderId}"))!.Status);
    }

    [Fact]
    public async Task HeldPayment_LooksLikeAnyOtherPaymentToTheCustomer()
    {
        using var host = Host();
        var merchant = Merchant(host);
        using var reviewer = await host.SignInReviewerAsync();
        var order = await CreateOrderAsync(merchant, reviewer, SandboxWalletScreening.HighRiskAddress);
        using var browser = Browser(host);

        await PayAsync(browser, order);

        var page = await browser.GetStringAsync(order.CheckoutUrl!.AbsolutePath);
        Assert.Contains("Payment received", page, StringComparison.Ordinal);
        Assert.Equal("OnHold", (await merchant.GetFromJsonAsync<OrderResponse>($"/v1/orders/{order.OrderId}"))!.Status);
        foreach (var word in new[] { "hold", "review", "reject", "frozen", "compliance", "risk", "sanction" })
        {
            Assert.DoesNotContain(word, page, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task ExpiredLink_TakesNoPayment()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 25, 6, 0, 0, TimeSpan.Zero));
        using var host = Host(clock: clock);
        var merchant = Merchant(host);
        var order = await CreateOrderAsync(merchant);
        using var browser = Browser(host);
        clock.Advance(TimeSpan.FromMinutes(16)); // past the 15-minute payment window

        var page = await browser.GetStringAsync(order.CheckoutUrl!.AbsolutePath);
        using var paid = await PayAsync(browser, order);

        Assert.Contains("This payment link has expired", page, StringComparison.Ordinal);
        Assert.DoesNotContain("<form", page, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.SeeOther, paid.StatusCode);
        Assert.Empty((await merchant.GetFromJsonAsync<List<LedgerEntryResponse>>($"/v1/orders/{order.OrderId}/ledger"))!);
    }

    [Fact]
    public async Task RefundedPayment_IsShownAsRefunded()
    {
        using var host = Host();
        var merchant = Merchant(host);
        var order = await CreateOrderAsync(merchant);
        using var browser = Browser(host);

        // Paid after the deadline, so the payment is refunded instead of converted.
        using var late = await merchant.PostAsJsonAsync(new Uri($"/v1/sandbox/orders/{order.OrderId}/pay", UriKind.Relative), new { lateByMinutes = 1 });
        late.EnsureSuccessStatusCode();

        Assert.Contains("Payment refunded", await browser.GetStringAsync(order.CheckoutUrl!.AbsolutePath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownAndAlteredLinks_GetTheSameNotFoundPage()
    {
        using var host = Host();
        var merchant = Merchant(host);
        var order = await CreateOrderAsync(merchant);
        using var browser = Browser(host);
        var token = order.CheckoutUrl!.Segments[^1];
        var altered = token[..^1] + (token[^1] == 'A' ? 'B' : 'A');
        var unknown = $"{Guid.NewGuid():N}{token[32..]}";

        using var alteredPage = await browser.GetAsync(new Uri($"/checkout/{altered}", UriKind.Relative));
        using var unknownPage = await browser.GetAsync(new Uri($"/checkout/{unknown}", UriKind.Relative));
        using var alteredStatus = await browser.GetAsync(new Uri($"/checkout/{altered}/status", UriKind.Relative));

        Assert.Equal((HttpStatusCode.NotFound, HttpStatusCode.NotFound, HttpStatusCode.NotFound), (alteredPage.StatusCode, unknownPage.StatusCode, alteredStatus.StatusCode));
        var alteredBody = WithoutNonce(await alteredPage.Content.ReadAsStringAsync());
        Assert.Equal(alteredBody, WithoutNonce(await unknownPage.Content.ReadAsStringAsync()));
        Assert.Contains("Payment link not found", alteredBody, StringComparison.Ordinal);
        Assert.DoesNotContain("10,000", alteredBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MerchantName_IsShownAsTextNeverAsMarkup()
    {
        using var host = Host(displayName: "<img src=x onerror=alert(1)>");
        var merchant = Merchant(host);
        var order = await CreateOrderAsync(merchant);
        using var browser = Browser(host);

        var page = await browser.GetStringAsync(order.CheckoutUrl!.AbsolutePath);

        Assert.DoesNotContain("<img src=x", page, StringComparison.Ordinal);
        Assert.Contains("&lt;img src=x onerror=alert(1)&gt;", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Page_IsLockedDown_AndLoadsNothingFromOtherSites()
    {
        using var host = Host();
        var merchant = Merchant(host);
        var order = await CreateOrderAsync(merchant);
        using var browser = Browser(host);

        using var response = await browser.GetAsync(new Uri(order.CheckoutUrl!.AbsolutePath, UriKind.Relative));
        var page = await response.Content.ReadAsStringAsync();

        var policy = response.Headers.GetValues("Content-Security-Policy").Single();
        var nonce = NonceInPolicy().Match(policy).Groups[1].Value;
        Assert.NotEmpty(nonce);
        Assert.Contains("default-src 'none'", policy, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", policy, StringComparison.Ordinal);
        Assert.Contains($"<style nonce=\"{nonce}\">", page, StringComparison.Ordinal);
        Assert.Contains($"<script nonce=\"{nonce}\">", page, StringComparison.Ordinal);
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("noindex", response.Headers.GetValues("X-Robots-Tag").Single(), StringComparison.Ordinal);
        Assert.DoesNotMatch("(src|href)=\"(https?:)?//", page); // no third-party resources (no return link is set here)
    }

    [Fact]
    public async Task StatusEndpoint_ReportsWhatTheCustomerMaySee()
    {
        using var host = Host();
        var merchant = Merchant(host);
        var order = await CreateOrderAsync(merchant);
        using var browser = Browser(host);
        var statusPath = order.CheckoutUrl!.AbsolutePath + "/status";

        var before = await browser.GetFromJsonAsync<CheckoutStatusResponse>(statusPath);
        await PayAsync(browser, order);
        var after = await browser.GetFromJsonAsync<CheckoutStatusResponse>(statusPath);

        Assert.Equal(("AwaitingPayment", 900L), (before!.Status, before.SecondsRemaining));
        Assert.Equal(("PaymentReceived", 0L), (after!.Status, after.SecondsRemaining));
    }

    [Theory]
    [InlineData("INR", "10000.00", "₹10,000.00")]
    [InlineData("INR", "100000", "₹1,00,000.00")]
    [InlineData("INR", "12345678.9", "₹1,23,45,678.90")]
    [InlineData("INR", "999.5", "₹999.50")]
    [InlineData("USD", "1234567.5", "1,234,567.50 USD")]
    [InlineData("JPY", "50000", "50,000 JPY")]
    public void Amounts_ReadTheWayCustomersWriteThem(string currency, string amount, string expected)
    {
        var money = new Money(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), SandboxAssets.CreateCatalog().Get(AssetId.Fiat(currency)));

        Assert.Equal(expected, CustomerFormat.Amount(money));
    }

    [Theory]
    [InlineData("Asia/Kolkata", "17:30 IST")]
    [InlineData("Europe/London", "12:00 UTC+00:00")]
    public void Deadlines_ShowTheLocalTime(string zone, string expected)
    {
        Assert.Equal(expected, CustomerFormat.TimeOfDay(new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero), TimeZoneInfo.FindSystemTimeZoneById(zone)));
    }

    private WebApplicationFactory<Program> Host(string? displayName = null, string? returnUrl = null, ManualTimeProvider? clock = null) =>
        factory.WithWebHostBuilder(builder =>
        {
            if (displayName is not null)
            {
                builder.UseSetting("Gateway:ApiKeys:Clients:0:DisplayName", displayName);
            }

            if (returnUrl is not null)
            {
                builder.UseSetting("Gateway:ApiKeys:Clients:0:CheckoutReturnUrl", returnUrl);
            }

            if (clock is not null)
            {
                builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(clock));
            }
        });

    private static HttpClient Merchant(WebApplicationFactory<Program> host)
    {
        var merchant = host.CreateClient();
        merchant.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GatewayApiFactory.ClientAKey);
        return merchant;
    }

    /// <summary>A customer's browser: no API key, and redirects are seen rather than followed.</summary>
    private static HttpClient Browser(WebApplicationFactory<Program> host) =>
        host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static Task<OrderResponse> CreateOrderAsync(HttpClient merchant, HttpClient? reviewer = null, string wallet = Address) =>
        ApiFlows.CreateOrderAsync(merchant, reviewer, wallet);

    private static Task<HttpResponseMessage> PayAsync(HttpClient browser, OrderResponse order) =>
        browser.PostAsync(
            new Uri(order.CheckoutUrl!.AbsolutePath + "/pay", UriKind.Relative),
            new FormUrlEncodedContent([new KeyValuePair<string, string>("method", "upi")]));

    private static string WithoutNonce(string page) => NonceInPage().Replace(page, "nonce=\"\"");

    [GeneratedRegex("'nonce-([^']+)'")]
    private static partial Regex NonceInPolicy();

    [GeneratedRegex("nonce=\"[^\"]*\"")]
    private static partial Regex NonceInPage();
}
