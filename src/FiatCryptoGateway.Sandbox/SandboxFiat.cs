using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FiatCryptoGateway.Application.OnRamp;
using FiatCryptoGateway.Application.Ports;
using FiatCryptoGateway.Application.Webhooks;
using FiatCryptoGateway.Domain;
using FiatCryptoGateway.Domain.Assets;
using FiatCryptoGateway.Domain.Orders;
using Microsoft.Extensions.Options;

namespace FiatCryptoGateway.Sandbox;

public sealed class SandboxOptions
{
    public const string SectionName = "Gateway:Sandbox";

    /// <summary>
    /// Secret for signing sandbox payment webhooks. If empty, a random one is generated at startup, and
    /// only the built-in simulator can sign events. Never commit a value.
    /// </summary>
    public string WebhookSecret { get; set; } = string.Empty;
}

public sealed class SandboxWebhookSecret
{
    public SandboxWebhookSecret(IOptions<SandboxOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var configured = options.Value.WebhookSecret;
        Value = string.IsNullOrWhiteSpace(configured) ? Convert.ToHexString(RandomNumberGenerator.GetBytes(32)) : configured;
    }

    public string Value { get; }
}

/// <summary>Creates fake checkout sessions and refunds. Idempotent per order.</summary>
public sealed class SandboxFiatProvider(TimeProvider time) : IFiatPaymentProvider
{
    public const string ProviderName = "sandbox-fiat";

    private readonly ConcurrentDictionary<Guid, FiatRefund> _refunds = new();

    public string Name => ProviderName;

    public Task<FiatPaymentSession> CreateSessionAsync(PaymentOrder order, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);
        var sessionId = $"fps_{order.OrderId:N}";
        return Task.FromResult(new FiatPaymentSession(sessionId, new Uri($"https://checkout.sandbox.invalid/{sessionId}")));
    }

    public Task<FiatRefund> RefundAsync(PaymentOrder order, Money amount, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);
        return Task.FromResult(_refunds.GetOrAdd(
            order.OrderId,
            _ => new FiatRefund(Name, $"rfd_{order.OrderId:N}", order.OrderId, amount, time.GetUtcNow())));
    }
}

/// <summary>The sandbox provider's webhook body. Amounts are strings so no precision is lost in JSON.</summary>
public sealed record SandboxPaymentEvent(
    string Id,
    string Type,
    Guid OrderId,
    string PaymentId,
    string Amount,
    string Currency,
    DateTimeOffset CapturedAt);

public enum WebhookResult
{
    Accepted,
    Duplicate,
    InvalidSignature,
    Invalid,
}

/// <summary>
/// Receives the sandbox provider's payment webhooks: verifies the signature over the raw body first,
/// parses second, claims the event ID so a redelivery is processed once, then records the payment.
/// </summary>
public sealed class SandboxFiatWebhookHandler(
    SandboxWebhookSecret secret,
    IWebhookInbox inbox,
    AssetCatalog catalog,
    OnRampService onRamp,
    TimeProvider time)
{
    public const string SignatureHeader = "X-Sandbox-Signature";
    public const string PaymentCaptured = "payment.captured";

    private static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<WebhookResult> HandleAsync(string body, string? signatureHeader, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (!WebhookSignature.Verify(secret.Value, signatureHeader, body, time.GetUtcNow(), Tolerance))
        {
            return WebhookResult.InvalidSignature;
        }

        if (!TryParse(body, out var paymentEvent, out var payment))
        {
            return WebhookResult.Invalid;
        }

        if (!await inbox.TryClaimAsync(SandboxFiatProvider.ProviderName, paymentEvent.Id, Sha256Hex(body), cancellationToken))
        {
            return WebhookResult.Duplicate;
        }

        try
        {
            await onRamp.RecordFiatPaymentAsync(payment, cancellationToken);
        }
        catch
        {
            // Let the provider's redelivery try again rather than losing the payment.
            await inbox.ReleaseAsync(SandboxFiatProvider.ProviderName, paymentEvent.Id, CancellationToken.None);
            throw;
        }

        return WebhookResult.Accepted;
    }

    /// <summary>Builds a signed event exactly as the sandbox provider would deliver it.</summary>
    public (string Body, string Signature) CreateSignedPaymentEvent(PaymentOrder order, Money? amount = null, DateTimeOffset? capturedAt = null)
    {
        ArgumentNullException.ThrowIfNull(order);
        var now = time.GetUtcNow();
        var paid = amount ?? order.Quote.SourceAmount;
        var paymentEvent = new SandboxPaymentEvent(
            $"evt_{Guid.NewGuid():N}",
            PaymentCaptured,
            order.OrderId,
            $"pay_{order.OrderId:N}",
            paid.ToFixedString(),
            paid.Asset.Id.Code,
            capturedAt ?? now);
        var body = JsonSerializer.Serialize(paymentEvent, Json);
        return (body, WebhookSignature.Sign(secret.Value, now, body));
    }

    private bool TryParse(string body, out SandboxPaymentEvent paymentEvent, out FiatPaymentConfirmation payment)
    {
        paymentEvent = null!;
        payment = null!;
        try
        {
            var parsed = JsonSerializer.Deserialize<SandboxPaymentEvent>(body, Json);
            if (parsed is null ||
                parsed.Type != PaymentCaptured ||
                string.IsNullOrWhiteSpace(parsed.Id) ||
                string.IsNullOrWhiteSpace(parsed.PaymentId) ||
                !decimal.TryParse(parsed.Amount, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount) ||
                !catalog.TryGet(AssetId.Fiat(parsed.Currency), out var currency))
            {
                return false;
            }

            paymentEvent = parsed;
            payment = new FiatPaymentConfirmation(
                SandboxFiatProvider.ProviderName, parsed.PaymentId, parsed.OrderId, new Money(amount, currency), parsed.CapturedAt);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (GatewayRuleException)
        {
            return false;
        }
    }

    private static string Sha256Hex(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
