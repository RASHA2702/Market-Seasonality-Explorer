using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.RegularExpressions;
using FiatCryptoGateway.Api.Checkout;
using FiatCryptoGateway.Api.Contracts;
using FiatCryptoGateway.Application.Payments;
using FiatCryptoGateway.Domain.Payments;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FiatCryptoGateway.Api.Tests;

/// <summary>A customer paying on the hosted page with the gateway's own processor, driven over HTTP as a browser would.</summary>
public sealed partial class CheckoutPaymentTests(GatewayApiFactory factory) : IClassFixture<GatewayApiFactory>
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task ThePage_OffersUpiAndCardForms_ThatNeverCarryAValue()
    {
        using var host = Host();
        using var merchant = Merchant(host);
        var order = await ApiFlows.CreateOrderAsync(merchant);
        using var browser = Browser(host);

        using var response = await browser.GetAsync(Path(order));
        var page = await response.Content.ReadAsStringAsync(None);

        Assert.Contains("Pay with UPI", page, StringComparison.Ordinal);
        Assert.Contains("Pay with a card", page, StringComparison.Ordinal);
        foreach (var attribute in new[] { "autocomplete=\"cc-number\"", "autocomplete=\"cc-exp\"", "autocomplete=\"cc-csc\"", "autocomplete=\"cc-name\"", "name=\"upi_address\"" })
        {
            Assert.Contains(attribute, page, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("Netbanking", page, StringComparison.Ordinal); // only the methods that have a network behind them
        Assert.DoesNotContain("Bank transfer", page, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"<input[^>]*type=""(text|password)""[^>]*\svalue=", page); // nothing typed is ever put back
        Assert.Matches(@"<input id=""card-code""[^>]*type=""password""", page); // the security code is masked as it is typed
        var policy = response.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("form-action 'self'", policy, StringComparison.Ordinal);
        Assert.Contains("default-src 'none'", policy, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithTheEarlierPartner_ThePageOffersNothingToTypeInto()
    {
        using var host = Host("SandboxPartner");
        using var merchant = Merchant(host);
        var order = await ApiFlows.CreateOrderAsync(merchant);
        using var browser = Browser(host);

        var page = await browser.GetStringAsync(Path(order));
        using var typed = await PostFormAsync(browser, order, "pay", Card("4111 1111 1111 1111"));

        Assert.DoesNotContain("card-number", page, StringComparison.Ordinal);
        Assert.Contains("/sandbox-pay", page, StringComparison.Ordinal);
        Assert.Contains("?problem=PAYMENT_METHOD_UNAVAILABLE", typed.Headers.Location!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACardWithoutAFurtherCheck_IsPaid_AndNothingTypedComesBack()
    {
        var logs = new CapturingLogs();
        using var host = Host(logs: logs);
        using var merchant = Merchant(host);
        var order = await ApiFlows.CreateOrderAsync(merchant);
        using var browser = Browser(host);

        using var paid = await PostFormAsync(browser, order, "pay", Card("3782 8224 6310 005", code: "7391", name: "Zebediah Quillfeather")); // an Amex has a 4-digit code

        Assert.Equal(HttpStatusCode.SeeOther, paid.StatusCode);
        Assert.Equal(order.CheckoutUrl, paid.Headers.Location);
        var page = await browser.GetStringAsync(Path(order));
        Assert.Contains("Payment received", page, StringComparison.Ordinal);
        var attempt = Assert.Single(await Attempts(host).ListForOrderAsync(order.OrderId, None));
        Assert.Equal((PaymentAttemptStatus.Captured, PaymentMethod.Card, "American Express ····0005"), (attempt.Status, attempt.Method, attempt.Instrument.Label));
        var ledger = await merchant.GetFromJsonAsync<List<LedgerEntryResponse>>(new Uri($"/v1/orders/{order.OrderId}/ledger", UriKind.Relative));
        Assert.Single(ledger!, entry => entry.Kind == "FIAT_RECEIVED");

        var everything = string.Join('\n', await paid.Content.ReadAsStringAsync(None), paid.Headers, page, string.Join('\n', logs.Lines));
        foreach (var secret in new[] { "378282246310005", "3782 8224 6310 005", "7391", "Zebediah", "Quillfeather" })
        {
            Assert.DoesNotContain(secret, everything, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ACardWithAOneTimePassword_ShowsTheCodeForm_RefusesAWrongCode_ThenPays()
    {
        using var host = Host();
        using var merchant = Merchant(host);
        var order = await ApiFlows.CreateOrderAsync(merchant);
        using var browser = Browser(host);

        using var started = await PostFormAsync(browser, order, "pay", Card("4242 4242 4242 4242"));
        var page = await browser.GetStringAsync(Path(order));

        Assert.Equal(order.CheckoutUrl, started.Headers.Location);
        Assert.Contains("Enter the code from your bank", page, StringComparison.Ordinal);
        Assert.Contains("Visa ····4242", page, StringComparison.Ordinal);
        Assert.Contains("3 tries left", page, StringComparison.Ordinal);
        Assert.Contains("In the sandbox the code is <code>123456</code>", page, StringComparison.Ordinal);
        Assert.Contains("data-attempt=\"AwaitingAuthentication\"", page, StringComparison.Ordinal);
        Assert.Contains("<details class=\"other\">", page, StringComparison.Ordinal); // another way to pay stays one click away

        // The page names the card by its last four digits and the sandbox help lists the published test numbers; nothing else typed is shown.
        var shown = SandboxHelp().Replace(page, string.Empty);
        Assert.DoesNotContain("4242 4242", shown, StringComparison.Ordinal);
        Assert.DoesNotContain("42424242", shown, StringComparison.Ordinal);
        Assert.DoesNotContain("Asha Verma", shown, StringComparison.Ordinal);
        var attemptId = AttemptIn(page);

        using var wrong = await PostFormAsync(browser, order, "authenticate", new() { ["attempt"] = attemptId, ["code"] = "000000" });
        var retry = await browser.GetStringAsync(wrong.Headers.Location!.PathAndQuery);

        Assert.EndsWith("?problem=WRONG_CODE", wrong.Headers.Location.ToString(), StringComparison.Ordinal);
        Assert.Contains("That code was not right", retry, StringComparison.Ordinal);
        Assert.Contains("2 tries left", retry, StringComparison.Ordinal);

        using var wrongAgain = await PostFormAsync(browser, order, "authenticate", new() { ["attempt"] = attemptId, ["code"] = "111111" });
        Assert.Contains("1 try left", await browser.GetStringAsync(wrongAgain.Headers.Location!.PathAndQuery), StringComparison.Ordinal);

        using var right = await PostFormAsync(browser, order, "authenticate", new() { ["attempt"] = attemptId, ["code"] = "123456" });

        Assert.Equal(order.CheckoutUrl, right.Headers.Location);
        Assert.Contains("Payment received", await browser.GetStringAsync(Path(order)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADeclinedCard_SaysWhy_AndTheCustomerCanTryAgain()
    {
        using var host = Host();
        using var merchant = Merchant(host);
        var order = await ApiFlows.CreateOrderAsync(merchant);
        using var browser = Browser(host);

        using var declined = await PostFormAsync(browser, order, "pay", Card("4000 0000 0000 9995"));
        var page = await browser.GetStringAsync(Path(order));

        Assert.Equal(order.CheckoutUrl, declined.Headers.Location);
        Assert.Contains("There was not enough money available", page, StringComparison.Ordinal);
        Assert.Contains("Pay with a card", page, StringComparison.Ordinal); // the forms are back
        Assert.Equal("AwaitingFiatPayment", (await merchant.GetFromJsonAsync<OrderResponse>(new Uri($"/v1/orders/{order.OrderId}", UriKind.Relative)))!.Status);

        using var again = await PostFormAsync(browser, order, "pay", Card("4111 1111 1111 1111"));

        Assert.Contains("Payment received", await browser.GetStringAsync(Path(order)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("4012 8888 8888 1881", "TEST_CARDS_ONLY", "only the listed test cards work")]
    [InlineData("4111 1111 1111 1112", "INVALID_CARD_NUMBER", "does not look right")]
    public async Task ARealOrMistypedCard_IsRefused_WithoutRepeatingIt_AndNothingIsKept(string number, string code, string words)
    {
        using var host = Host();
        using var merchant = Merchant(host);
        var order = await ApiFlows.CreateOrderAsync(merchant);
        using var browser = Browser(host);

        using var refused = await PostFormAsync(browser, order, "pay", Card(number));
        var page = await browser.GetStringAsync(refused.Headers.Location!.PathAndQuery);

        Assert.EndsWith($"?problem={code}", refused.Headers.Location.ToString(), StringComparison.Ordinal);
        Assert.Contains(words, page, StringComparison.Ordinal);
        var digits = number.Replace(" ", string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(digits[^4..], string.Join('\n', refused.Headers.Location.ToString(), page), StringComparison.Ordinal);
        Assert.Empty(await Attempts(host).ListForOrderAsync(order.OrderId, None));
    }

    [Theory]
    [InlineData("1/28", true)]
    [InlineData("01/2028", true)]
    [InlineData("12 / 28", true)]
    [InlineData("13/28", false)]
    [InlineData("12-28", false)]
    [InlineData("1228x", false)]
    public async Task TheExpiryDate_IsReadInTheWaysPeopleWriteIt(string expiry, bool accepted)
    {
        using var host = Host();
        using var merchant = Merchant(host);
        var order = await ApiFlows.CreateOrderAsync(merchant);
        using var browser = Browser(host);

        using var response = await PostFormAsync(browser, order, "pay", Card("4111 1111 1111 1111", expiry: expiry));

        var attempts = await Attempts(host).ListForOrderAsync(order.OrderId, None);
        Assert.Equal(accepted, attempts.Count == 1);
        Assert.Equal(accepted, response.Headers.Location == order.CheckoutUrl);
        Assert.Equal(accepted, !response.Headers.Location!.ToString().Contains("INVALID_EXPIRY", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OverlongFields_AreRefusedAtTheDoor_AndNeverReachTheProcessor()
    {
        using var host = Host();
        using var merchant = Merchant(host);
        var order = await ApiFlows.CreateOrderAsync(merchant);
        using var browser = Browser(host);

        using var longNumber = await PostFormAsync(browser, order, "pay", Card(new string('4', 24)));
        using var longCode = await PostFormAsync(browser, order, "pay", Card("4111 1111 1111 1111", code: "73912"));
        using var longAddress = await PostFormAsync(browser, order, "pay", new() { ["method"] = "upi", ["upi_address"] = new string('a', 81) + "@bank" });
        using var noMethod = await PostFormAsync(browser, order, "pay", new() { ["method"] = "cash" });

        Assert.EndsWith("?problem=INVALID_CARD_NUMBER", longNumber.Headers.Location!.ToString(), StringComparison.Ordinal);
        Assert.EndsWith("?problem=INVALID_SECURITY_CODE", longCode.Headers.Location!.ToString(), StringComparison.Ordinal);
        Assert.EndsWith("?problem=INVALID_UPI_ADDRESS", longAddress.Headers.Location!.ToString(), StringComparison.Ordinal);
        Assert.EndsWith("?problem=PAYMENT_METHOD_UNAVAILABLE", noMethod.Headers.Location!.ToString(), StringComparison.Ordinal);
        Assert.Empty(await Attempts(host).ListForOrderAsync(order.OrderId, None));
    }

    [Fact]
    public async Task UpiPayment_ShowsTheRequest_ThenPaysWhenItIsApproved()
    {
        using var host = Host();
        using var merchant = Merchant(host);
        var order = await ApiFlows.CreateOrderAsync(merchant);
        using var browser = Browser(host);

        using var started = await PostFormAsync(browser, order, "pay", new() { ["method"] = "upi", ["upi_address"] = "success@sandbox" });
        var page = await browser.GetStringAsync(Path(order));

        Assert.Equal(order.CheckoutUrl, started.Headers.Location);
        Assert.Contains("Approve the request in your UPI app", page, StringComparison.Ordinal);
        Assert.Contains("We sent a request for ₹10,000.00 to success@sandbox", page, StringComparison.Ordinal);
        Assert.Contains("name=\"approve\" value=\"yes\"", page, StringComparison.Ordinal); // sandbox: stands in for the phone
        var status = await browser.GetFromJsonAsync<CheckoutStatusResponse>(new Uri(Path(order) + "/status", UriKind.Relative));
        Assert.Equal(("AwaitingPayment", "AwaitingAuthentication"), (status!.Status, status.Attempt));

        using var approved = await PostFormAsync(browser, order, "authenticate", new() { ["attempt"] = AttemptIn(page), ["approve"] = "yes" });

        Assert.Equal(order.CheckoutUrl, approved.Headers.Location);
        Assert.Contains("Payment received", await browser.GetStringAsync(Path(order)), StringComparison.Ordinal);
        Assert.Null((await browser.GetFromJsonAsync<CheckoutStatusResponse>(new Uri(Path(order) + "/status", UriKind.Relative)))!.Attempt);
    }

    [Theory]
    [InlineData("insufficient@sandbox", "yes", "There was not enough money available")]
    [InlineData("success@sandbox", "no", "You declined the request, so nothing was charged")]
    [InlineData("timeout@sandbox", "yes", "The request timed out, so we could not confirm a charge")]
    public async Task UpiPayments_ThatEndBadly_SayWhy(string address, string answer, string words)
    {
        using var host = Host();
        using var merchant = Merchant(host);
        var order = await ApiFlows.CreateOrderAsync(merchant);
        using var browser = Browser(host);
        using var started = await PostFormAsync(browser, order, "pay", new() { ["method"] = "upi", ["upi_address"] = address });
        var attemptId = AttemptIn(await browser.GetStringAsync(Path(order)));

        using var answered = await PostFormAsync(browser, order, "authenticate", new() { ["attempt"] = attemptId, ["approve"] = answer });
        var page = await browser.GetStringAsync(Path(order));

        Assert.Contains(words, page, StringComparison.Ordinal);
        Assert.Contains("Pay with UPI", page, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("invalid@sandbox", "That UPI address could not be found")]
    [InlineData("unavailable@sandbox", "We could not reach the payment network, so we could not confirm a charge")]
    public async Task UpiAddresses_ThatFailAtOnce_SayWhy(string address, string words)
    {
        using var host = Host();
        using var merchant = Merchant(host);
        var order = await ApiFlows.CreateOrderAsync(merchant);
        using var browser = Browser(host);

        using var response = await PostFormAsync(browser, order, "pay", new() { ["method"] = "upi", ["upi_address"] = address });

        Assert.Contains(words, await browser.GetStringAsync(Path(order)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARealUpiAddress_IsRefused_WithoutRepeatingIt()
    {
        using var host = Host();
        using var merchant = Merchant(host);
        var order = await ApiFlows.CreateOrderAsync(merchant);
        using var browser = Browser(host);

        using var refused = await PostFormAsync(browser, order, "pay", new() { ["method"] = "upi", ["upi_address"] = "someone@okbank" });
        var page = await browser.GetStringAsync(refused.Headers.Location!.PathAndQuery);

        Assert.EndsWith("?problem=TEST_UPI_ONLY", refused.Headers.Location.ToString(), StringComparison.Ordinal);
        Assert.Contains("only the listed test UPI addresses work", page, StringComparison.Ordinal);
        Assert.DoesNotContain("someone@okbank", page, StringComparison.Ordinal);
        Assert.Empty(await Attempts(host).ListForOrderAsync(order.OrderId, None));
    }

    [Fact]
    public async Task AnAttemptThatRanOutOfTime_ShowsTheTimeout_AndTheFormsAgain()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 25, 6, 0, 0, TimeSpan.Zero));
        using var host = Host(clock: clock);
        using var merchant = Merchant(host);
        var order = await ApiFlows.CreateOrderAsync(merchant);
        using var browser = Browser(host);
        using var started = await PostFormAsync(browser, order, "pay", new() { ["method"] = "upi", ["upi_address"] = "success@sandbox" });

        clock.Advance(TimeSpan.FromMinutes(11)); // past the attempt's ten minutes, inside the order's fifteen
        var page = await browser.GetStringAsync(Path(order));
        var status = await browser.GetFromJsonAsync<CheckoutStatusResponse>(new Uri(Path(order) + "/status", UriKind.Relative));

        Assert.Contains("The request timed out, so " + CustomerMessages.CannotConfirmACharge, page, StringComparison.Ordinal);
        Assert.DoesNotContain("nothing was charged", page, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Pay with a card", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Approve the request in your UPI app", page, StringComparison.Ordinal);
        Assert.Equal("Expired", status!.Attempt);
    }

    [Fact]
    public async Task AnAnswerForNoAttemptOfThisOrder_ChangesNothing_AndAMalformedOneIsRefused()
    {
        using var host = Host();
        using var merchant = Merchant(host);
        var order = await ApiFlows.CreateOrderAsync(merchant);
        var other = await ApiFlows.CreateOrderAsync(merchant);
        using var browser = Browser(host);
        using var started = await PostFormAsync(browser, other, "pay", Card("4242 4242 4242 4242"));
        var othersAttempt = AttemptIn(await browser.GetStringAsync(Path(other)));

        using var unknown = await PostFormAsync(browser, order, "authenticate", new() { ["attempt"] = Guid.NewGuid().ToString(), ["code"] = "123456" });
        using var wrongOrder = await PostFormAsync(browser, order, "authenticate", new() { ["attempt"] = othersAttempt, ["code"] = "123456" });
        using var malformed = await PostFormAsync(browser, order, "authenticate", new() { ["attempt"] = "not-a-guid", ["code"] = "123456" });

        Assert.Equal(order.CheckoutUrl, unknown.Headers.Location);
        Assert.Equal(order.CheckoutUrl, wrongOrder.Headers.Location);
        Assert.EndsWith("?problem=INVALID_INSTRUMENT", malformed.Headers.Location!.ToString(), StringComparison.Ordinal);
        Assert.Equal("AwaitingAuthentication", (await Attempts(host).ListForOrderAsync(other.OrderId, None)).Single().Status.ToString());
    }

    [Fact]
    public async Task APaidOrExpiredLink_TakesNoPayment_AndAPostedFormIsRedirected()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 25, 6, 0, 0, TimeSpan.Zero));
        using var host = Host(clock: clock);
        using var merchant = Merchant(host);
        var paid = await ApiFlows.CreateOrderAsync(merchant);
        var expired = await ApiFlows.CreateOrderAsync(merchant);
        using var browser = Browser(host);
        using var first = await PostFormAsync(browser, paid, "pay", Card("4111 1111 1111 1111"));

        using var again = await PostFormAsync(browser, paid, "pay", Card("4111 1111 1111 1111")); // a double submission
        clock.Advance(TimeSpan.FromMinutes(16));
        using var late = await PostFormAsync(browser, expired, "pay", Card("4111 1111 1111 1111"));

        Assert.Equal(paid.CheckoutUrl, again.Headers.Location);
        Assert.Equal(expired.CheckoutUrl, late.Headers.Location);
        Assert.Single(await Attempts(host).ListForOrderAsync(paid.OrderId, None));
        Assert.Empty(await Attempts(host).ListForOrderAsync(expired.OrderId, None));
    }

    [Fact]
    public async Task OnlyWordsFromTheFixedList_AreEverShownFromTheAddressBar()
    {
        using var host = Host();
        using var merchant = Merchant(host);
        var order = await ApiFlows.CreateOrderAsync(merchant);
        using var browser = Browser(host);

        var injected = await browser.GetStringAsync(Path(order) + "?problem=%3Cscript%3Ealert(1)%3C/script%3E");
        var invented = await browser.GetStringAsync(Path(order) + "?problem=YOUR_CARD_4111_WAS_STOLEN");

        foreach (var page in new[] { injected, invented })
        {
            Assert.DoesNotContain("alert(1)", page, StringComparison.Ordinal);
            Assert.DoesNotContain("STOLEN", page, StringComparison.Ordinal);
            Assert.DoesNotContain("class=\"notice\"", page, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EveryDeclineCode_AndEveryRefusalTheFormCanSend_HasAMessage_ThatOnlyClaimsNoChargeWhenThatIsCertain()
    {
        var codes = typeof(DeclineCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral)
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToList();
        Assert.NotEmpty(codes);
        Assert.All(codes, code => Assert.Contains(code, CustomerMessages.DeclineCodesWithMessages));

        var refusals = new[]
        {
            "INVALID_CARD_NUMBER", "TEST_CARDS_ONLY", "INVALID_EXPIRY", "INVALID_SECURITY_CODE", "INVALID_INSTRUMENT", "INVALID_UPI_ADDRESS", "TEST_UPI_ONLY",
            "WRONG_CODE", "PAYMENT_METHOD_UNAVAILABLE", "ORDER_NOT_PAYABLE", "ORDER_ALREADY_PAID", "PAYMENT_IN_PROGRESS", "TOO_MANY_ATTEMPTS",
        };
        Assert.All(refusals, code => Assert.Contains(code, CustomerMessages.RefusalCodesWithMessages));
        Assert.All(codes.Select(CustomerMessages.ForDecline).Concat(refusals.Select(CustomerMessages.ForRefusal)), text => Assert.False(string.IsNullOrWhiteSpace(text)));

        // "Nothing was charged" is said only where we know it is true: the customer said no, or the bank could not confirm it was them,
        // so no money was ever asked for. Nowhere else.
        var claimsNoCharge = codes.Where(code => CustomerMessages.ForDecline(code).Contains("nothing was charged", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(
            new[] { DeclineCodes.CustomerDeclined, DeclineCodes.AuthenticationFailed }.Order(),
            claimsNoCharge.Order());

        // Where the network may have acted without our hearing (unreachable, timed out, an approval given up), the customer is told
        // that no charge could be confirmed, and that a debit will be reversed by the bank.
        var cannotBeSure = new[] { DeclineCodes.RailUnavailable, DeclineCodes.TimedOut, DeclineCodes.AuthorizationLapsed };
        Assert.All(cannotBeSure, code =>
        {
            var text = CustomerMessages.ForDecline(code);
            Assert.Contains("we could not confirm a charge. If you were debited, it will be reversed by your bank.", text, StringComparison.Ordinal);
            Assert.DoesNotContain("nothing was charged", text, StringComparison.OrdinalIgnoreCase);
        });

        // Every other message, and one for a code nobody has met yet, says nothing about charges at all.
        var silent = codes.Except(claimsNoCharge).Except(cannotBeSure).Select(CustomerMessages.ForDecline).Append(CustomerMessages.ForDecline("SOMETHING_NEW"));
        Assert.All(silent, text => Assert.DoesNotContain("charge", text, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WhenTheNetworkCannotBeReached_TheCustomerIsNeverToldNothingWasCharged()
    {
        using var host = Host();
        using var merchant = Merchant(host);
        var order = await ApiFlows.CreateOrderAsync(merchant);
        using var browser = Browser(host);

        using var response = await PostFormAsync(browser, order, "pay", new() { ["method"] = "upi", ["upi_address"] = "unavailable@sandbox" });
        var page = await browser.GetStringAsync(Path(order));

        Assert.Contains("we could not confirm a charge. If you were debited, it will be reversed by your bank.", page, StringComparison.Ordinal);
        Assert.DoesNotContain("nothing was charged", page, StringComparison.OrdinalIgnoreCase);
    }

    private WebApplicationFactory<Program> Host(string provider = "Processor", ManualTimeProvider? clock = null, CapturingLogs? logs = null) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:Fiat:Provider", provider);
            builder.ConfigureTestServices(services =>
            {
                if (clock is not null)
                {
                    services.AddSingleton<TimeProvider>(clock);
                }

                if (logs is not null)
                {
                    services.AddLogging(logging => logging.AddProvider(logs));
                }
            });
        });

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

    private static string Path(OrderResponse order) => order.CheckoutUrl!.AbsolutePath;

    private static Task<HttpResponseMessage> PostFormAsync(HttpClient browser, OrderResponse order, string action, Dictionary<string, string> fields) =>
        browser.PostAsync(new Uri($"{Path(order)}/{action}", UriKind.Relative), new FormUrlEncodedContent(fields));

    private static Dictionary<string, string> Card(string number, string expiry = "12/28", string code = "737", string name = "Asha Verma") =>
        new() { ["method"] = "card", ["card_number"] = number, ["card_expiry"] = expiry, ["card_code"] = code, ["card_name"] = name };

    private static string AttemptIn(string page)
    {
        var match = AttemptField().Match(page);
        Assert.True(match.Success, "The page has no attempt to answer.");
        return match.Groups[1].Value;
    }

    [GeneratedRegex("name=\"attempt\" value=\"([0-9a-f-]{36})\"", RegexOptions.CultureInvariant)]
    private static partial Regex AttemptField();

    [GeneratedRegex("<details class=\"sandbox-help\">.*?</details>", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex SandboxHelp();

    /// <summary>Everything the host logged, kept so a test can check that nothing a customer typed was written down.</summary>
    private sealed class CapturingLogs : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _lines = new();

        public IReadOnlyList<string> Lines => [.. _lines];

        public ILogger CreateLogger(string categoryName) => new Capture(_lines);

        public void Dispose()
        {
        }

        private sealed class Capture(ConcurrentQueue<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                lines.Enqueue($"{formatter(state, exception)} {exception}");
        }
    }
}
