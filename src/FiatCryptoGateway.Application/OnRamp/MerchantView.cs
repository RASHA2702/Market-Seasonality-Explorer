using FiatCryptoGateway.Domain.Orders;

namespace FiatCryptoGateway.Application.OnRamp;

public sealed record MerchantEvent(int Sequence, string? From, string To, DateTimeOffset OccurredAt, string Reason);

/// <summary>
/// What a merchant may see of an order. Review, rejection and freezing all appear as <c>OnHold</c> with the
/// reason <c>ON_HOLD</c>, and consecutive on-hold steps collapse into one, so nothing a merchant receives
/// reveals that a payment was rejected or reported (which could tip off the customer). The merchant's events
/// are numbered 1, 2, 3 ... on their own, because gaps in the internal numbering would show that hidden steps
/// happened. Reviewers use the full view.
/// </summary>
public static class MerchantView
{
    public const string OnHold = "OnHold";
    public const string OnHoldReason = "ON_HOLD";
    public const string RefundStartedReason = "REFUND_STARTED";

    public static string Status(OrderStatus status) => IsHold(status) ? OnHold : status.ToString();

    public static IReadOnlyList<MerchantEvent> Events(PaymentOrder order) =>
        Visible(order).Select(visible => visible.Event).ToList().AsReadOnly();

    /// <summary>
    /// The merchant's events, each with the sequence number of the order event it came from. Events are only
    /// ever appended, and whether one is visible depends only on earlier events, so the numbering never changes.
    /// </summary>
    internal static IReadOnlyList<(MerchantEvent Event, int SourceSequence)> Visible(PaymentOrder order)
    {
        ArgumentNullException.ThrowIfNull(order);
        var visible = new List<(MerchantEvent Event, int SourceSequence)>();
        foreach (var orderEvent in order.Events)
        {
            if (Describe(orderEvent, visible.Count + 1) is { } merchantEvent &&
                (visible.Count == 0 || visible[^1].Event.To != merchantEvent.To))
            {
                visible.Add((merchantEvent, orderEvent.Sequence));
            }
        }

        return visible.AsReadOnly();
    }

    /// <summary>The merchant-visible form of one event, or null when it changes nothing the merchant can see.</summary>
    private static MerchantEvent? Describe(OrderEvent orderEvent, int merchantSequence)
    {
        var from = orderEvent.From is { } previous ? Status(previous) : null;
        var to = Status(orderEvent.To);
        if (from == to)
        {
            return null;
        }

        var reason = IsHold(orderEvent.To) ? OnHoldReason
            : orderEvent.From is { } held && IsHold(held) && orderEvent.To == OrderStatus.RefundPending ? RefundStartedReason
            : orderEvent.Reason;
        return new MerchantEvent(merchantSequence, from, to, orderEvent.OccurredAt, reason);
    }

    private static bool IsHold(OrderStatus status) =>
        status is OrderStatus.ComplianceReview or OrderStatus.ComplianceRejected or OrderStatus.Frozen;
}
