using FiatCryptoGateway.Api.Auth;
using FiatCryptoGateway.Application.Checkout;
using FiatCryptoGateway.Application.Configuration;
using FiatCryptoGateway.Application.OnRamp;
using FiatCryptoGateway.Application.Payments;
using FiatCryptoGateway.Domain;
using FiatCryptoGateway.Domain.Orders;
using FiatCryptoGateway.Sandbox;
using Microsoft.Extensions.Options;

namespace FiatCryptoGateway.Api.Checkout;

public sealed record CheckoutStatusResponse(string Status, long SecondsRemaining);

/// <summary>
/// The pages customers pay on. The signed link is the only credential: no merchant key, no login and no cookie, so
/// there is nothing for another site to ride on (no cross-site request forgery).
/// </summary>
public static class CheckoutEndpoints
{
    public static IEndpointRouteBuilder MapCheckout(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/checkout/{token}", ShowPage);
        app.MapGet("/checkout/{token}/status", GetStatus);
        app.MapPost("/checkout/{token}/pay", PayInSandbox);
        return app;
    }

    private static async Task<IResult> ShowPage(
        string token,
        HttpContext http,
        CheckoutService checkout,
        IOptions<ApiKeyOptions> merchants,
        IOptions<CheckoutOptions> options,
        IOptions<GatewaySafetyOptions> safety,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var order = await checkout.FindOrderAsync(token, cancellationToken);
        if (order is null)
        {
            return CheckoutPage.NotFound(http);
        }

        var now = time.GetUtcNow();
        var merchant = merchants.Value.Clients.FirstOrDefault(client => client.ClientId == order.ClientId);
        var model = new CheckoutPageModel(
            merchant?.DisplayName ?? order.ClientId,
            order.OrderId.ToString("N")[..8].ToUpperInvariant(),
            CustomerFormat.Amount(order.Quote.SourceAmount),
            CustomerView.Status(order, now),
            CustomerFormat.TimeOfDay(order.PaymentDeadline, options.Value.Zone),
            SecondsRemaining(order, now),
            token,
            merchant?.CheckoutReturnUrl is { } returnUrl ? WithOrder(returnUrl, order.OrderId) : null,
            safety.Value.Mode == GatewayMode.Sandbox);
        return CheckoutPage.Render(http, model);
    }

    private static async Task<IResult> GetStatus(
        string token,
        HttpContext http,
        CheckoutService checkout,
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
        return Results.Ok(new CheckoutStatusResponse(CustomerView.Status(order, now).ToString(), SecondsRemaining(order, now)));
    }

    /// <summary>
    /// Sandbox only: the customer's payment, simulated through the same signed provider event a real payment would
    /// produce. Then back to the page, which shows the result (post, redirect, get), so a refresh never pays twice.
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

        if (CustomerView.Status(order, time.GetUtcNow()) == CustomerPaymentStatus.AwaitingPayment)
        {
            if (fiat.Value.Provider == FiatProviderChoice.Processor)
            {
                try
                {
                    // The path a real payment takes, with the test UPI address that is always approved.
                    await processor.PayWithTestUpiAsync(order.OrderId, cancellationToken);
                }
                catch (GatewayRuleException)
                {
                    // Already paid, or a payment is being completed: the page after the redirect shows where things stand.
                }
            }
            else
            {
                var (body, signature) = webhooks.CreateSignedPaymentEvent(order);
                await webhooks.HandleAsync(body, signature, cancellationToken);
            }
        }

        CheckoutPage.ApplySecurityHeaders(http.Response);
        http.Response.Headers.Location = links.Url(order.OrderId).ToString();
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
}
