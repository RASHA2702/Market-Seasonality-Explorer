using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FiatCryptoGateway.Application.OnRamp;
using FiatCryptoGateway.Application.Ports;
using FiatCryptoGateway.Application.Reconciliation;
using FiatCryptoGateway.Application.Webhooks;
using FiatCryptoGateway.Domain;
using FiatCryptoGateway.Domain.Assets;
using FiatCryptoGateway.Domain.Ledger;
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

/// <summary>
/// Creates fake checkout sessions and refunds (idempotent per order), and keeps its own record of the payments it
/// captured and the refunds it made, independent of the gateway's books, from which it publishes a daily
/// settlement report as a real provider would.
/// </summary>
public sealed class SandboxFiatProvider(TimeProvider time) : IFiatPaymentProvider, ISettlementReportSource
{
    public const string ProviderName = "sandbox-fiat";

    private readonly ConcurrentDictionary<Guid, FiatRefund> _refunds = new();
    private readonly ConcurrentDictionary<string, FiatPaymentConfirmation> _captures = new(StringComparer.Ordinal);

    public string Name => ProviderName;

    public string Provider => ProviderName;

    public Task<SettlementReport> GetReportAsync(DateOnly day, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken)
    {
        var payments = _captures.Values
            .Where(payment => payment.CapturedAt >= windowStart && payment.CapturedAt < windowEnd)
            .Select(payment => new SettlementLine(EntryKinds.FiatReceived, payment.ProviderPaymentId, payment.OrderId, payment.Amount, payment.CapturedAt));
        var refunds = _refunds.Values
            .Where(refund => refund.RefundedAt >= windowStart && refund.RefundedAt < windowEnd)
            .Select(refund => new SettlementLine(EntryKinds.FiatRefunded, refund.RefundId, refund.OrderId, refund.Amount, refund.RefundedAt));
        IReadOnlyList<SettlementLine> lines = [.. payments.Concat(refunds).OrderBy(line => line.OccurredAt).ThenBy(line => line.Reference, StringComparer.Ordinal)];
        return Task.FromResult(new SettlementReport(ProviderName, day, lines));
    }

    /// <summary>The provider has taken the customer's money, whether or not its webhook ever reaches the gateway.</summary>
    internal void RecordCapture(FiatPaymentConfirmation payment) => _captures[payment.ProviderPaymentId] = payment;

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
    SandboxFiatProvider provider,
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

    /// <summary>
    /// Captures the customer's payment at the sandbox provider and builds its signed event exactly as the provider
    /// would deliver it. The capture is in the provider's records (and its settlement report) even if the event is
    /// never delivered.
    /// </summary>
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
        provider.RecordCapture(new FiatPaymentConfirmation(
            SandboxFiatProvider.ProviderName, paymentEvent.PaymentId, order.OrderId, paid, paymentEvent.CapturedAt));
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
