using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FiatCryptoGateway.Api.Auth;
using FiatCryptoGateway.Api.Contracts;
using FiatCryptoGateway.Application;
using FiatCryptoGateway.Application.Compliance;
using FiatCryptoGateway.Application.Configuration;
using FiatCryptoGateway.Application.OnRamp;
using FiatCryptoGateway.Application.Ports;
using FiatCryptoGateway.Domain;
using FiatCryptoGateway.Domain.Assets;
using FiatCryptoGateway.Domain.Ledger;
using FiatCryptoGateway.Domain.Orders;
using FiatCryptoGateway.Sandbox;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace FiatCryptoGateway.Api.Endpoints;

public static class GatewayEndpoints
{
    public const string IdempotencyKeyHeader = "Idempotency-Key";
    public const string IdempotentReplayedHeader = "Idempotent-Replayed";

    public static IEndpointRouteBuilder MapGatewayEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/v1/environment", GetEnvironment);

        // Called by the payment provider, not by API clients: authenticated by its signature.
        app.MapPost("/v1/webhooks/sandbox-fiat", ReceiveSandboxFiatWebhook);

        var api = app.MapGroup("/v1").RequireAuthorization(ApiPolicies.Merchant).AddEndpointFilter<GatewayErrorFilter>();
        api.MapGet("/assets", ListAssets);
        api.MapPost("/quotes", CreateQuote);
        api.MapPost("/orders", CreateOrder);
        api.MapGet("/orders/{orderId:guid}", GetOrder);
        api.MapGet("/orders/{orderId:guid}/ledger", GetOrderLedger);

        // Simulations that stand in for the outside world while no real provider is connected.
        var sandbox = app.MapGroup("/v1/sandbox").RequireAuthorization(ApiPolicies.Merchant).AddEndpointFilter<GatewayErrorFilter>();
        sandbox.MapPost("/orders/{orderId:guid}/pay", SimulatePayment);
        sandbox.MapPost("/orders/{orderId:guid}/confirm", SimulateConfirmations);

        // The compliance officer's queue and decisions, across all merchants. Reviewer keys only.
        var review = app.MapGroup("/v1/review").RequireAuthorization(ApiPolicies.Reviewer).AddEndpointFilter<GatewayErrorFilter>();
        review.MapGet("/queue", GetReviewQueue);
        review.MapGet("/orders/{orderId:guid}", GetReviewOrder);
        review.MapPost("/orders/{orderId:guid}/approve", (Guid orderId, ReviewActionRequest? request, ClaimsPrincipal user, ComplianceReviewService reviews, CancellationToken ct) =>
            Review(orderId, request, ReviewActionKind.Approve, user, reviews, ct));
        review.MapPost("/orders/{orderId:guid}/reject", (Guid orderId, ReviewActionRequest? request, ClaimsPrincipal user, ComplianceReviewService reviews, CancellationToken ct) =>
            Review(orderId, request, ReviewActionKind.Reject, user, reviews, ct));
        review.MapPost("/orders/{orderId:guid}/freeze", (Guid orderId, ReviewActionRequest? request, ClaimsPrincipal user, ComplianceReviewService reviews, CancellationToken ct) =>
            Review(orderId, request, ReviewActionKind.FreezeAndReport, user, reviews, ct));
        review.MapPost("/orders/{orderId:guid}/release", (Guid orderId, ReviewActionRequest? request, ClaimsPrincipal user, ComplianceReviewService reviews, CancellationToken ct) =>
            Review(orderId, request, ReviewActionKind.ReleaseForRefund, user, reviews, ct));
        review.MapGet("/orders/{orderId:guid}/webhooks", GetWebhookDeliveries);
        review.MapGet("/ledger/balances", GetLedgerBalances);
        return app;
    }

    private static EnvironmentResponse GetEnvironment(IOptions<GatewaySafetyOptions> safety, OnRampProviders providers) =>
        new(safety.Value.Mode.ToString(), RealFundsMovement: false, providers.Fiat.Name, providers.Liquidity.Name, providers.Custody.Name);

    private static IEnumerable<AssetResponse> ListAssets(AssetCatalog catalog) =>
        catalog.Assets
            .OrderBy(asset => asset.Kind)
            .ThenBy(asset => asset.Id.ToString(), StringComparer.Ordinal)
            .Select(AssetResponse.From);

    private static async Task<IResult> CreateQuote(
        CreateQuoteRequest? request,
        ClaimsPrincipal user,
        OnRampService onRamp,
        CancellationToken cancellationToken)
    {
        if (request is null ||
            string.IsNullOrWhiteSpace(request.SourceCurrency) ||
            request.SourceAmount is not { } amount ||
            string.IsNullOrWhiteSpace(request.DestinationAsset))
        {
            throw new GatewayRuleException("INVALID_REQUEST", "sourceCurrency, sourceAmount and destinationAsset are required.");
        }

        var quote = await onRamp.CreateQuoteAsync(
            user.GetClientId(), new CreateQuoteCommand(request.SourceCurrency, amount, request.DestinationAsset), cancellationToken);
        return Results.Json(QuoteResponse.From(quote), statusCode: StatusCodes.Status201Created);
    }

    /// <summary>
    /// Creates an order exactly once per Idempotency-Key. A retry with the same key and body replays the
    /// first response; the same key with a different body is refused.
    /// </summary>
    private static async Task<IResult> CreateOrder(
        HttpContext http,
        OnRampService onRamp,
        IIdempotencyStore idempotency,
        IOptions<JsonOptions> jsonOptions,
        CancellationToken cancellationToken)
    {
        var clientId = http.User.GetClientId();
        var json = jsonOptions.Value.SerializerOptions;
        var key = http.Request.Headers[IdempotencyKeyHeader].ToString();
        if (key.Length is 0 or > 255)
        {
            return Error(StatusCodes.Status400BadRequest, "IDEMPOTENCY_KEY_REQUIRED", "Send a unique Idempotency-Key header (1-255 characters).");
        }

        string body;
        using (var reader = new StreamReader(http.Request.Body, Encoding.UTF8))
        {
            body = await reader.ReadToEndAsync(cancellationToken);
        }

        var requestHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
        var existing = await idempotency.TryClaimAsync(clientId, key, requestHash, cancellationToken);
        if (existing is not null)
        {
            if (!string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal))
            {
                return Error(StatusCodes.Status422UnprocessableEntity, "IDEMPOTENCY_KEY_REUSED", "This Idempotency-Key was used with a different request.");
            }

            if (existing.Response is null)
            {
                return Error(StatusCodes.Status409Conflict, "REQUEST_IN_PROGRESS", "A request with this Idempotency-Key is still running.");
            }

            http.Response.Headers[IdempotentReplayedHeader] = "true";
            return Results.Content(existing.Response.Body, "application/json", Encoding.UTF8, existing.Response.StatusCode);
        }

        StoredResponse response;
        try
        {
            response = await ExecuteCreateOrderAsync(body, clientId, onRamp, json, cancellationToken);
        }
        catch
        {
            // An unexpected failure produced no response; free the key so the client can retry.
            await idempotency.ReleaseAsync(clientId, key, CancellationToken.None);
            throw;
        }

        await idempotency.CompleteAsync(clientId, key, response, cancellationToken);
        return Results.Content(response.Body, "application/json", Encoding.UTF8, response.StatusCode);
    }

    private static async Task<StoredResponse> ExecuteCreateOrderAsync(
        string body,
        string clientId,
        OnRampService onRamp,
        JsonSerializerOptions json,
        CancellationToken cancellationToken)
    {
        try
        {
            var request = JsonSerializer.Deserialize<CreateOrderRequest>(body, json);
            if (request?.QuoteId is not { } quoteId ||
                string.IsNullOrWhiteSpace(request.CustomerId) ||
                string.IsNullOrWhiteSpace(request.DestinationAddress))
            {
                return Stored(StatusCodes.Status400BadRequest, "INVALID_REQUEST", "quoteId, customerId and destinationAddress are required.");
            }

            var order = await onRamp.CreateOrderAsync(
                clientId, new CreateOrderCommand(quoteId, request.CustomerId, request.DestinationAddress), cancellationToken);
            return new StoredResponse(StatusCodes.Status201Created, JsonSerializer.Serialize(OrderResponse.From(order), json));
        }
        catch (JsonException)
        {
            return Stored(StatusCodes.Status400BadRequest, "INVALID_JSON", "The request body is not valid JSON.");
        }
        catch (GatewayRuleException exception)
        {
            return Stored(StatusCodes.Status422UnprocessableEntity, exception.Code, exception.Message);
        }
        catch (ResourceNotFoundException exception)
        {
            return Stored(StatusCodes.Status404NotFound, exception.Code, exception.Message);
        }

        StoredResponse Stored(int status, string code, string message) =>
            new(status, JsonSerializer.Serialize(new ErrorResponse(new ErrorDetail(code, message)), json));
    }

    private static async Task<OrderResponse> GetOrder(
        Guid orderId,
        ClaimsPrincipal user,
        OnRampService onRamp,
        CancellationToken cancellationToken) =>
        OrderResponse.From(await onRamp.GetOrderAsync(user.GetClientId(), orderId, cancellationToken));

    private static async Task<IEnumerable<LedgerEntryResponse>> GetOrderLedger(
        Guid orderId,
        ClaimsPrincipal user,
        OnRampService onRamp,
        ILedgerStore ledger,
        CancellationToken cancellationToken)
    {
        var order = await onRamp.GetOrderAsync(user.GetClientId(), orderId, cancellationToken);
        var entries = await ledger.ListAsync(order.OrderId, cancellationToken);
        return entries.Select(LedgerEntryResponse.From);
    }

    private static async Task<IResult> ReceiveSandboxFiatWebhook(
        HttpContext http,
        SandboxFiatWebhookHandler handler,
        CancellationToken cancellationToken)
    {
        string body;
        using (var reader = new StreamReader(http.Request.Body, Encoding.UTF8))
        {
            body = await reader.ReadToEndAsync(cancellationToken);
        }

        var signature = http.Request.Headers[SandboxFiatWebhookHandler.SignatureHeader].ToString();
        var result = await handler.HandleAsync(body, signature, cancellationToken);
        return result switch
        {
            WebhookResult.InvalidSignature => Results.Unauthorized(),
            WebhookResult.Invalid => Results.BadRequest(new WebhookAcknowledgement("invalid")),
            // Duplicates are acknowledged so the provider stops redelivering.
            _ => Results.Ok(new WebhookAcknowledgement(result == WebhookResult.Duplicate ? "duplicate" : "accepted")),
        };
    }

    private static async Task<IResult> SimulatePayment(
        Guid orderId,
        SimulatePaymentRequest? request,
        ClaimsPrincipal user,
        OnRampService onRamp,
        SandboxFiatWebhookHandler webhooks,
        CancellationToken cancellationToken)
    {
        var clientId = user.GetClientId();
        var order = await onRamp.GetOrderAsync(clientId, orderId, cancellationToken);
        var amount = request?.Amount is { } paid ? new Money(paid, order.Quote.SourceAmount.Asset) : null;
        DateTimeOffset? capturedAt = request?.LateByMinutes is { } late ? order.PaymentDeadline.AddMinutes(late) : null;

        // Goes through the same signature check and handler a real provider webhook would.
        var (body, signature) = webhooks.CreateSignedPaymentEvent(order, amount, capturedAt);
        var result = await webhooks.HandleAsync(body, signature, cancellationToken);
        if (result != WebhookResult.Accepted)
        {
            throw new InvalidOperationException($"The sandbox payment event was not accepted: {result}.");
        }

        return Results.Ok(OrderResponse.From(await onRamp.GetOrderAsync(clientId, orderId, cancellationToken)));
    }

    private static async Task<OrderResponse> SimulateConfirmations(
        Guid orderId,
        SimulateConfirmationsRequest? request,
        ClaimsPrincipal user,
        OnRampService onRamp,
        ICustodyProvider custody,
        CancellationToken cancellationToken)
    {
        var clientId = user.GetClientId();
        var order = await onRamp.GetOrderAsync(clientId, orderId, cancellationToken);
        var transfer = order.Transfer
            ?? throw new GatewayRuleException("ORDER_STATE_CONFLICT", "The order has no on-chain transfer to confirm yet.");
        var confirmations = request?.Confirmations ?? custody.RequiredConfirmations(order.Quote.DestinationAmount.Asset);
        return OrderResponse.From(
            await onRamp.RecordConfirmationsAsync(clientId, orderId, transfer.TransactionHash, confirmations, cancellationToken));
    }

    private static async Task<IEnumerable<ReviewQueueItemResponse>> GetReviewQueue(
        ComplianceReviewService reviews,
        CancellationToken cancellationToken) =>
        (await reviews.GetQueueAsync(cancellationToken)).Select(ReviewQueueItemResponse.From);

    private static async Task<ReviewOrderResponse> GetReviewOrder(
        Guid orderId,
        ComplianceReviewService reviews,
        CancellationToken cancellationToken) =>
        ReviewOrderResponse.From(await reviews.GetOrderAsync(orderId, cancellationToken));

    private static async Task<ReviewOrderResponse> Review(
        Guid orderId,
        ReviewActionRequest? request,
        ReviewActionKind kind,
        ClaimsPrincipal user,
        ComplianceReviewService reviews,
        CancellationToken cancellationToken) =>
        ReviewOrderResponse.From(await reviews.ReviewAsync(
            orderId, kind, user.GetClientId(), request?.Reason ?? string.Empty, request?.ReportReference, cancellationToken));

    /// <summary>Whole-ledger balances across all merchants. Reviewer keys only.</summary>
    private static async Task<IEnumerable<WebhookDeliveryResponse>> GetWebhookDeliveries(
        Guid orderId,
        ComplianceReviewService reviews,
        IOutbox outbox,
        CancellationToken cancellationToken)
    {
        await reviews.GetOrderAsync(orderId, cancellationToken); // 404 for an unknown order
        return (await outbox.ListForOrderAsync(orderId, cancellationToken)).Select(WebhookDeliveryResponse.From);
    }

    private static async Task<LedgerBalancesResponse> GetLedgerBalances(ILedgerStore ledger, CancellationToken cancellationToken)
    {
        var balances = LedgerBalances.Compute(await ledger.ListAsync(orderId: null, cancellationToken));
        var balanced = balances.GroupBy(balance => balance.Asset).All(asset => asset.Sum(balance => balance.DebitMinusCredit) == 0);
        return new LedgerBalancesResponse(
            balanced,
            [.. balances.Select(balance => new BalanceResponse(
                balance.Account,
                balance.Asset.ToString(),
                balance.DebitMinusCredit.ToString(CultureInfo.InvariantCulture)))]);
    }

    private static IResult Error(int status, string code, string message) =>
        Results.Json(new ErrorResponse(new ErrorDetail(code, message)), statusCode: status);
}

/// <summary>Turns rule violations into 422 and missing resources into 404, with a stable error code.</summary>
internal sealed class GatewayErrorFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (GatewayRuleException exception)
        {
            return Results.Json(
                new ErrorResponse(new ErrorDetail(exception.Code, exception.Message)),
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }
        catch (ResourceNotFoundException exception)
        {
            return Results.Json(
                new ErrorResponse(new ErrorDetail(exception.Code, exception.Message)),
                statusCode: StatusCodes.Status404NotFound);
        }
    }
}
