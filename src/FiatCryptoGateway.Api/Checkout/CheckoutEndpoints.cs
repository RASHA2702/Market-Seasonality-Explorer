using System.Globalization;
using System.Text.RegularExpressions;
using FiatCryptoGateway.Api.Auth;
using FiatCryptoGateway.Application;
using FiatCryptoGateway.Application.Checkout;
using FiatCryptoGateway.Application.Configuration;
using FiatCryptoGateway.Application.OnRamp;
using FiatCryptoGateway.Application.Payments;
using FiatCryptoGateway.Domain;
using FiatCryptoGateway.Domain.Orders;
using FiatCryptoGateway.Domain.Payments;
using FiatCryptoGateway.Sandbox;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace FiatCryptoGateway.Api.Checkout;

/// <summary>What the page polls: the order's customer-facing status and the latest attempt's, so it reloads when either changes.</summary>
public sealed record CheckoutStatusResponse(string Status, long SecondsRemaining, string? Attempt = null);

/// <summary>
/// The pages customers pay on. The signed link is the only credential: no merchant key, no login and no cookie, so
/// there is nothing for another site to ride on (no cross-site request forgery). What a customer types goes to the
/// processor once and is never put back into a page, a redirect address or a log: every form is answered with a redirect
/// back to the page, and the only thing added to the address is a short code from a fixed list.
/// </summary>
public static partial class CheckoutEndpoints
{
    // A payment form is a few short fields. Anything bigger is not one.
    private const int MaxFormBytes = 4096;

    public static IEndpointRouteBuilder MapCheckout(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/checkout/{token}", ShowPage);
        app.MapGet("/checkout/{token}/status", GetStatus);
        app.MapPost("/checkout/{token}/pay", Pay);
        app.MapPost("/checkout/{token}/authenticate", Authenticate);
        app.MapPost("/checkout/{token}/sandbox-pay", PayInSandbox);
        return app;
    }

    private static async Task<IResult> ShowPage(
        string token,
        HttpContext http,
        CheckoutService checkout,
        PaymentProcessor processor,
        PaymentRailSet rails,
        IOptions<ApiKeyOptions> merchants,
        IOptions<CheckoutOptions> options,
        IOptions<GatewaySafetyOptions> safety,
        IOptions<FiatProviderOptions> fiat,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var order = await checkout.FindOrderAsync(token, cancellationToken);
        if (order is null)
        {
            return CheckoutPage.NotFound(http);
        }

        var now = time.GetUtcNow();
        var status = CustomerView.Status(order, now);
        var merchant = merchants.Value.Clients.FirstOrDefault(client => client.ClientId == order.ClientId);
        var attempt = status == CustomerPaymentStatus.AwaitingPayment
            ? AttemptModel(await processor.LatestAttemptAsync(order.OrderId, cancellationToken), now)
            : null;
        var problem = http.Request.Query["problem"].ToString();
        var model = new CheckoutPageModel(
            merchant?.DisplayName ?? order.ClientId,
            order.OrderId.ToString("N")[..8].ToUpperInvariant(),
            CustomerFormat.Amount(order.Quote.SourceAmount),
            status,
            CustomerFormat.TimeOfDay(order.PaymentDeadline, options.Value.Zone),
            SecondsRemaining(order, now),
            token,
            merchant?.CheckoutReturnUrl is { } returnUrl ? WithOrder(returnUrl, order.OrderId) : null,
            safety.Value.Mode == GatewayMode.Sandbox,
            Methods(fiat.Value, rails),
            attempt,
            CustomerMessages.IsKnownRefusal(problem) ? problem : null);
        return CheckoutPage.Render(http, model);
    }

    private static async Task<IResult> GetStatus(
        string token,
        HttpContext http,
        CheckoutService checkout,
        PaymentProcessor processor,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        CheckoutPage.ApplySecurityHeaders(http.Response);
        var order = await checkout.FindOrderAsync(token, cancellationToken);
        if (order is null)
        {
            return Results.NotFound();
        }

        var now = time.GetUtcNow();
        var status = CustomerView.Status(order, now);
        var attempt = status == CustomerPaymentStatus.AwaitingPayment
            ? AttemptModel(await processor.LatestAttemptAsync(order.OrderId, cancellationToken), now)?.Status
            : null;
        return Results.Ok(new CheckoutStatusResponse(status.ToString(), SecondsRemaining(order, now), attempt));
    }

    /// <summary>
    /// The customer's typed payment. Whatever happens, the answer is a redirect back to the page: a refusal adds only a
    /// code from a fixed list, so a refresh never pays twice and nothing typed comes back.
    /// </summary>
    private static async Task<IResult> Pay(
        string token,
        HttpContext http,
        CheckoutService checkout,
        CheckoutLinks links,
        PaymentProcessor processor,
        IOptions<FiatProviderOptions> fiat,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var order = await checkout.FindOrderAsync(token, cancellationToken);
        if (order is null)
        {
            return CheckoutPage.NotFound(http);
        }

        string? problem = null;
        if (fiat.Value.Provider != FiatProviderChoice.Processor)
        {
            problem = "PAYMENT_METHOD_UNAVAILABLE";
        }
        else if (CustomerView.Status(order, time.GetUtcNow()) == CustomerPaymentStatus.AwaitingPayment)
        {
            problem = await StartAsync(http, order.OrderId, processor, cancellationToken);
        }

        return Back(http, links, order.OrderId, problem);
    }

    /// <summary>The customer's answer to their bank's check: a one-time password, or (sandbox only) a stand-in for their UPI app.</summary>
    private static async Task<IResult> Authenticate(
        string token,
        HttpContext http,
        CheckoutService checkout,
        CheckoutLinks links,
        PaymentProcessor processor,
        IOptions<GatewaySafetyOptions> safety,
        CancellationToken cancellationToken)
    {
        var order = await checkout.FindOrderAsync(token, cancellationToken);
        if (order is null)
        {
            return CheckoutPage.NotFound(http);
        }

        var form = await ReadFormAsync(http, cancellationToken);
        var answer = form is null ? null : ParseAnswer(form, safety.Value.Mode == GatewayMode.Sandbox);
        string? problem = null;
        if (answer is null || !Guid.TryParse(form!["attempt"].ToString(), out var attemptId))
        {
            problem = "INVALID_INSTRUMENT";
        }
        else
        {
            try
            {
                var view = await processor.AuthenticateAsync(order.OrderId, attemptId, answer, cancellationToken);
                if (answer is OtpResponse && view is { Status: PaymentAttemptStatus.AwaitingAuthentication })
                {
                    problem = "WRONG_CODE"; // still waiting for a code, so the one just sent was not accepted
                }
            }
            catch (ResourceNotFoundException)
            {
                // Not an attempt of this order: the page shows where things really stand.
            }
            catch (GatewayRuleException exception)
            {
                problem = KnownOrNull(exception.Code);
            }
        }

        return Back(http, links, order.OrderId, problem);
    }

    /// <summary>
    /// Sandbox only: pays without typing anything, through the same path a typed payment takes (or, with the earlier
    /// simulated partner, through its signed payment event). Then back to the page, which shows the result.
    /// </summary>
    private static async Task<IResult> PayInSandbox(
        string token,
        HttpContext http,
        CheckoutService checkout,
        CheckoutLinks links,
        SandboxFiatWebhookHandler webhooks,
        PaymentProcessor processor,
        IOptions<FiatProviderOptions> fiat,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var order = await checkout.FindOrderAsync(token, cancellationToken);
        if (order is null)
        {
            return CheckoutPage.NotFound(http);
        }

        string? problem = null;
        if (CustomerView.Status(order, time.GetUtcNow()) == CustomerPaymentStatus.AwaitingPayment)
        {
            if (fiat.Value.Provider == FiatProviderChoice.Processor)
            {
                try
                {
                    await processor.PayWithTestUpiAsync(order.OrderId, cancellationToken);
                }
                catch (GatewayRuleException exception)
                {
                    problem = KnownOrNull(exception.Code); // already paid, or a payment is being completed
                }
            }
            else
            {
                var (body, signature) = webhooks.CreateSignedPaymentEvent(order);
                await webhooks.HandleAsync(body, signature, cancellationToken);
            }
        }

        return Back(http, links, order.OrderId, problem);
    }

    private static async Task<string?> StartAsync(HttpContext http, Guid orderId, PaymentProcessor processor, CancellationToken cancellationToken)
    {
        var form = await ReadFormAsync(http, cancellationToken);
        if (form is null)
        {
            return "INVALID_INSTRUMENT";
        }

        var input = ParseInstrument(form, out var problem);
        if (input is null)
        {
            return problem;
        }

        try
        {
            await processor.StartAsync(orderId, input, cancellationToken);
            return null;
        }
        catch (GatewayRuleException exception)
        {
            return KnownOrNull(exception.Code) ?? "INVALID_INSTRUMENT";
        }
    }

    /// <summary>
    /// The form, or null if it is not one or is too big to be one. The size limit is set before anything is read, so a
    /// huge body is never buffered.
    /// </summary>
    private static async Task<IFormCollection?> ReadFormAsync(HttpContext http, CancellationToken cancellationToken)
    {
        if (!http.Request.HasFormContentType)
        {
            return null;
        }

        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = MaxFormBytes;
        }

        try
        {
            return await http.Request.ReadFormAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidDataException or BadHttpRequestException or IOException)
        {
            return null;
        }
    }

    /// <summary>What the customer typed, as the processor takes it. A problem is a code from a fixed list, never the input.</summary>
    private static PaymentInstrumentInput? ParseInstrument(IFormCollection form, out string? problem)
    {
        problem = null;
        switch (form["method"].ToString())
        {
            case "upi":
                var address = form["upi_address"].ToString();
                if (address.Length is 0 or > 80)
                {
                    problem = "INVALID_UPI_ADDRESS";
                    return null;
                }

                return new UpiInput(address);
            case "card":
                var number = form["card_number"].ToString();
                var securityCode = form["card_code"].ToString();
                if (number.Length is 0 or > 23)
                {
                    problem = "INVALID_CARD_NUMBER";
                    return null;
                }

                if (!TryParseExpiry(form["card_expiry"].ToString(), out var month, out var year))
                {
                    problem = "INVALID_EXPIRY";
                    return null;
                }

                if (securityCode.Length is 0 or > 4)
                {
                    problem = "INVALID_SECURITY_CODE";
                    return null;
                }

                var holder = form["card_name"].ToString().Trim();
                return new CardInput(number, month, year, securityCode, holder.Length == 0 ? null : holder[..Math.Min(holder.Length, 60)]);
            default:
                problem = "PAYMENT_METHOD_UNAVAILABLE";
                return null;
        }
    }

    private static AuthenticationResponse? ParseAnswer(IFormCollection form, bool sandbox)
    {
        var code = form["code"].ToString().Trim();
        if (code.Length is > 0 and <= 10)
        {
            return new OtpResponse(code);
        }

        // In the sandbox there is no phone, so the page's buttons stand in for the customer's UPI app.
        return sandbox && form["approve"].ToString() is "yes" or "no" ? new ApprovalResponse(form["approve"].ToString() == "yes") : null;
    }

    private static bool TryParseExpiry(string text, out int month, out int year)
    {
        month = 0;
        year = 0;
        var match = Expiry().Match(text);
        if (!match.Success)
        {
            return false;
        }

        month = int.Parse(match.Groups["month"].Value, CultureInfo.InvariantCulture);
        year = int.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture);
        year = year < 100 ? 2000 + year : year;
        return true;
    }

    /// <summary>The latest attempt, in the terms the page needs. Out of time counts as ended, even before the sweep ends it.</summary>
    internal static CheckoutAttemptModel? AttemptModel(PaymentAttemptView? view, DateTimeOffset now)
    {
        if (view is null)
        {
            return null;
        }

        var outOfTime = view.Status is PaymentAttemptStatus.Started or PaymentAttemptStatus.AwaitingAuthentication && now >= view.ExpiresAt;
        var step = outOfTime
            ? CheckoutAttemptStep.Ended
            : view.Status switch
            {
                PaymentAttemptStatus.AwaitingAuthentication when view.Challenge == ChallengeKind.Otp => CheckoutAttemptStep.EnterCode,
                PaymentAttemptStatus.AwaitingAuthentication when view.Challenge == ChallengeKind.UpiApproval => CheckoutAttemptStep.ApproveInUpiApp,
                PaymentAttemptStatus.Started or PaymentAttemptStatus.AwaitingAuthentication or PaymentAttemptStatus.Authorized => CheckoutAttemptStep.Finishing,
                PaymentAttemptStatus.Declined or PaymentAttemptStatus.AuthenticationFailed or PaymentAttemptStatus.Failed or PaymentAttemptStatus.Expired =>
                    view.DeclineCode == DeclineCodes.Superseded ? CheckoutAttemptStep.None : CheckoutAttemptStep.Ended,
                _ => CheckoutAttemptStep.None, // captured or refunded: the order's own status says what happened
            };
        return new CheckoutAttemptModel(
            step,
            view.AttemptId,
            view.InstrumentLabel,
            view.TriesLeft ?? 0,
            outOfTime ? DeclineCodes.TimedOut : view.DeclineCode,
            outOfTime ? PaymentAttemptStatus.Expired.ToString() : view.Status.ToString());
    }

    /// <summary>The ways to pay the page offers: what the processor has a network for. With the earlier partner there are none to type into.</summary>
    private static IReadOnlyList<PaymentMethod> Methods(FiatProviderOptions fiat, PaymentRailSet rails) =>
        fiat.Provider == FiatProviderChoice.Processor ? [.. rails.All.Select(rail => rail.Method).Distinct().Order()] : [];

    private static string? KnownOrNull(string code) => CustomerMessages.IsKnownRefusal(code) ? code : null;

    private static IResult Back(HttpContext http, CheckoutLinks links, Guid orderId, string? problem)
    {
        CheckoutPage.ApplySecurityHeaders(http.Response);
        var page = links.Url(orderId).ToString();
        http.Response.Headers.Location = problem is null ? page : $"{page}?problem={problem}"; // a code from our own list
        return Results.StatusCode(StatusCodes.Status303SeeOther);
    }

    private static long SecondsRemaining(PaymentOrder order, DateTimeOffset now) =>
        order.Status == OrderStatus.AwaitingFiatPayment ? Math.Max(0, (long)(order.PaymentDeadline - now).TotalSeconds) : 0;

    private static Uri WithOrder(Uri returnUrl, Guid orderId)
    {
        var builder = new UriBuilder(returnUrl);
        var parameter = $"order={orderId}";
        builder.Query = string.IsNullOrEmpty(builder.Query) ? parameter : $"{builder.Query.TrimStart('?')}&{parameter}";
        return builder.Uri;
    }

    [GeneratedRegex(@"^\s*(?<month>\d{1,2})\s*/\s*(?<year>\d{2}|\d{4})\s*$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex Expiry();
}
