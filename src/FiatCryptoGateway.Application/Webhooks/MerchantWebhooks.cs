using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using FiatCryptoGateway.Application.OnRamp;
using FiatCryptoGateway.Application.Ports;
using FiatCryptoGateway.Domain.Orders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FiatCryptoGateway.Application.Webhooks;

public sealed class WebhookOptions
{
    public const string SectionName = "Gateway:Webhooks";

    public bool Enabled { get; set; } = true;

    public int IntervalSeconds { get; set; } = 5;

    public int MaxAttempts { get; set; } = 10;

    public int BaseRetrySeconds { get; set; } = 30;

    public int MaxRetrySeconds { get; set; } = 3_600;

    /// <summary>How long one merchant endpoint may take to answer.</summary>
    public int SendTimeoutSeconds { get; set; } = 10;

    /// <summary>How long a claimed batch stays reserved for one instance. Must outlast a whole batch of timeouts.</summary>
    public int LeaseSeconds { get; set; } = 300;

    public int BatchSize { get; set; } = 20;
}

/// <summary>
/// Refuses settings under which two instances could send the same notification at once: a batch is sent one
/// message at a time, so its lease must outlast every message in it timing out.
/// </summary>
public sealed class WebhookOptionsValidator : IValidateOptions<WebhookOptions>
{
    public ValidateOptionsResult Validate(string? name, WebhookOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        if (options.IntervalSeconds < 1 || options.MaxAttempts < 1 || options.BaseRetrySeconds < 1 ||
            options.SendTimeoutSeconds < 1 || options.BatchSize < 1)
        {
            failures.Add("Webhook IntervalSeconds, MaxAttempts, BaseRetrySeconds, SendTimeoutSeconds and BatchSize must be at least 1.");
        }

        if (options.MaxRetrySeconds < options.BaseRetrySeconds)
        {
            failures.Add("Webhook MaxRetrySeconds must be at least BaseRetrySeconds.");
        }

        if ((long)options.LeaseSeconds <= (long)options.BatchSize * options.SendTimeoutSeconds)
        {
            failures.Add(
                $"Webhook LeaseSeconds ({options.LeaseSeconds}) must exceed BatchSize x SendTimeoutSeconds " +
                $"({options.BatchSize} x {options.SendTimeoutSeconds}), or another instance could resend a message still being sent.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

/// <summary>Builds merchant notifications from order events, using the merchant view (no compliance detail).</summary>
public static class MerchantNotifications
{
    public const string StatusChanged = "order.status_changed";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// One message per new event a merchant can see (events after the first <paramref name="storedEvents"/>);
    /// events invisible to merchants produce none. The payload's <c>sequence</c> is the merchant's own event
    /// number, matching the order's events in the API; the message keeps the internal number for ordering.
    /// </summary>
    public static IReadOnlyList<OutboxMessage> ForNewEvents(PaymentOrder order, int storedEvents)
    {
        ArgumentNullException.ThrowIfNull(order);
        var messages = new List<OutboxMessage>();
        foreach (var (visible, sourceSequence) in MerchantView.Visible(order).Where(v => v.SourceSequence > storedEvents))
        {
            var messageId = Guid.NewGuid();
            var payload = JsonSerializer.Serialize(
                new StatusChangedNotification(
                    messageId,
                    StatusChanged,
                    visible.OccurredAt,
                    new StatusChangedData(order.OrderId, visible.Sequence, visible.To, visible.From, visible.Reason, visible.OccurredAt)),
                Json);
            messages.Add(new OutboxMessage(messageId, order.ClientId, order.OrderId, sourceSequence, StatusChanged, payload, 0, visible.OccurredAt));
        }

        return messages.AsReadOnly();
    }

    private sealed record StatusChangedNotification(Guid Id, string Type, DateTimeOffset CreatedAt, StatusChangedData Data);

    private sealed record StatusChangedData(
        Guid OrderId,
        int Sequence,
        string Status,
        string? PreviousStatus,
        string Reason,
        DateTimeOffset OccurredAt);
}

/// <summary>
/// Sends due notifications, signed with each merchant's secret (same scheme as <see cref="WebhookSignature"/>).
/// A failure is retried with exponential backoff; after the last attempt the message is recorded as dead.
/// Delivery is at least once, so merchants should deduplicate on the event ID.
/// </summary>
public sealed partial class MerchantWebhookDispatcher(
    IOutbox outbox,
    IMerchantWebhookTargets targets,
    IWebhookSender sender,
    IOptions<WebhookOptions> options,
    TimeProvider time,
    ILogger<MerchantWebhookDispatcher> logger)
{
    /// <summary>Sends one batch of due messages and returns how many were claimed.</summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "A failing merchant endpoint must become a retry, never stop delivery to other merchants.")]
    public async Task<int> DispatchDueAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var messages = await outbox.ClaimDueAsync(
            time.GetUtcNow(), TimeSpan.FromSeconds(settings.LeaseSeconds), settings.BatchSize, cancellationToken);
        foreach (var message in messages)
        {
            // Each message is signed when it is sent, so its timestamp stays within the merchant's tolerance.
            var now = time.GetUtcNow();
            if (targets.Find(message.ClientId) is not { } target)
            {
                await outbox.MarkDeadAsync(message.MessageId, "NO_WEBHOOK_CONFIGURED", now, cancellationToken);
                continue;
            }

            WebhookDeliveryResult result;
            try
            {
                var signature = WebhookSignature.Sign(target.Secret, now, message.Payload);
                result = await sender.SendAsync(target.Url, message.MessageId, message.Payload, signature, cancellationToken);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Includes timeouts, which surface as cancellations that nobody asked for.
                result = WebhookDeliveryResult.Failed(exception.GetType().Name);
            }

            now = time.GetUtcNow();
            if (result.Succeeded)
            {
                await outbox.MarkDeliveredAsync(message.MessageId, now, cancellationToken);
            }
            else if (message.Attempts + 1 >= settings.MaxAttempts)
            {
                LogGaveUp(logger, message.MessageId, message.ClientId, result.Error);
                await outbox.MarkDeadAsync(message.MessageId, result.Error ?? "FAILED", now, cancellationToken);
            }
            else
            {
                await outbox.ScheduleRetryAsync(message.MessageId, now + Backoff(message.Attempts + 1, settings), result.Error ?? "FAILED", cancellationToken);
            }
        }

        return messages.Count;
    }

    /// <summary>30 s, 60 s, 120 s ... capped at <see cref="WebhookOptions.MaxRetrySeconds"/>.</summary>
    public static TimeSpan Backoff(int attempt, WebhookOptions settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return TimeSpan.FromSeconds(Math.Min(settings.BaseRetrySeconds * Math.Pow(2, Math.Max(0, attempt - 1)), settings.MaxRetrySeconds));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Gave up delivering webhook {MessageId} to {ClientId}: {Error}.")]
    private static partial void LogGaveUp(ILogger logger, Guid messageId, string clientId, string? error);
}
