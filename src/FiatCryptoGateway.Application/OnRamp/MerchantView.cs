using FiatCryptoGateway.Domain.Orders;

namespace FiatCryptoGateway.Application.OnRamp;

public sealed record MerchantEvent(int Sequence, string? From, string To, DateTimeOffset OccurredAt, string Reason);

/// <summary>
/// What a merchant may see of an order. Review, rejection and freezing all appear as <c>OnHold</c> with the
/// reason <c>ON_HOLD</c>, and consecutive on-hold steps collapse into one, so nothing a merchant receives
/// reveals that a payment was rejected or reported (which could tip off the customer). Reviewers use the
/// full view.
/// </summary>
public static class MerchantView
{
    public const string OnHold = "OnHold";
    public const string OnHoldReason = "ON_HOLD";
    public const string RefundStartedReason = "REFUND_STARTED";

    public static string Status(OrderStatus status) => IsHold(status) ? OnHold : status.ToString();

    public static IReadOnlyList<MerchantEvent> Events(PaymentOrder order)
    {
        ArgumentNullException.ThrowIfNull(order);
        var visible = new List<MerchantEvent>();
        foreach (var orderEvent in order.Events)
        {
            if (Describe(orderEvent) is { } merchantEvent &&
                (visible.Count == 0 || visible[^1].To != merchantEvent.To))
            {
                visible.Add(merchantEvent);
            }
        }

        return visible.AsReadOnly();
    }

    /// <summary>The merchant-visible form of one event, or null when it changes nothing the merchant can see.</summary>
    public static MerchantEvent? Describe(OrderEvent orderEvent)
    {
        ArgumentNullException.ThrowIfNull(orderEvent);
        var from = orderEvent.From is { } previous ? Status(previous) : null;
        var to = Status(orderEvent.To);
        if (from == to)
        {
            return null;
        }

        var reason = IsHold(orderEvent.To) ? OnHoldReason
            : orderEvent.From is { } held && IsHold(held) && orderEvent.To == OrderStatus.RefundPending ? RefundStartedReason
            : orderEvent.Reason;
        return new MerchantEvent(orderEvent.Sequence, from, to, orderEvent.OccurredAt, reason);
    }

    private static bool IsHold(OrderStatus status) =>
        status is OrderStatus.ComplianceReview or OrderStatus.ComplianceRejected or OrderStatus.Frozen;
}
