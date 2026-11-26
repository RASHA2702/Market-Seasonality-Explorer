using FiatCryptoGateway.Application.Configuration;
using FiatCryptoGateway.Application.OnRamp;
using FiatCryptoGateway.Application.Ports;
using FiatCryptoGateway.Domain.Orders;
using Microsoft.Extensions.Options;

namespace FiatCryptoGateway.Application.Compliance;

public sealed record ReviewQueueItem(PaymentOrder Order, DateTimeOffset HeldSince, TimeSpan Waiting, bool Overdue);

/// <summary>
/// The compliance officer's work list and decisions. Held orders are those waiting for a person
/// (<see cref="OrderStatus.ComplianceReview"/>) and rejected orders whose money must be frozen and reported
/// (<see cref="OrderStatus.ComplianceRejected"/>). Every decision is recorded on the order with who made it
/// and why, and saved atomically with the state change.
/// </summary>
public sealed class ComplianceReviewService(
    IOrderStore orders,
    IOrderLocks locks,
    OnRampService onRamp,
    IOptions<ComplianceOptions> options,
    TimeProvider time)
{
    private static readonly OrderStatus[] HeldStatuses = [OrderStatus.ComplianceReview, OrderStatus.ComplianceRejected];

    /// <summary>Held orders, longest-waiting first, flagged when they exceed the review time limit.</summary>
    public async Task<IReadOnlyList<ReviewQueueItem>> GetQueueAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var limit = TimeSpan.FromHours(options.Value.ReviewSlaHours);
        var items = new List<ReviewQueueItem>();
        foreach (var orderId in await orders.FindByStatusAsync(HeldStatuses, cancellationToken))
        {
            if (await orders.FindAsync(orderId, cancellationToken) is not { } order || !HeldStatuses.Contains(order.Status))
            {
                continue;
            }

            var heldSince = order.Events[^1].OccurredAt;
            items.Add(new ReviewQueueItem(order, heldSince, now - heldSince, now - heldSince > limit));
        }

        return items.OrderBy(item => item.HeldSince).ToList().AsReadOnly();
    }

    /// <summary>
    /// Frozen orders, frozen longest ago first. Their money waits for a person to release it for refund once that
    /// is allowed; there is no time limit on that, so none is flagged overdue.
    /// </summary>
    public async Task<IReadOnlyList<ReviewQueueItem>> GetFrozenAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var items = new List<ReviewQueueItem>();
        foreach (var orderId in await orders.FindByStatusAsync([OrderStatus.Frozen], cancellationToken))
        {
            if (await orders.FindAsync(orderId, cancellationToken) is { Status: OrderStatus.Frozen } order)
            {
                var frozenSince = order.Events[^1].OccurredAt;
                items.Add(new ReviewQueueItem(order, frozenSince, now - frozenSince, Overdue: false));
            }
        }

        return items.OrderBy(item => item.HeldSince).ToList().AsReadOnly();
    }

    public async Task<PaymentOrder> GetOrderAsync(Guid orderId, CancellationToken cancellationToken) =>
        await orders.FindAsync(orderId, cancellationToken)
            ?? throw new ResourceNotFoundException("ORDER_NOT_FOUND", "The order does not exist.");

    public async Task<PaymentOrder> ReviewAsync(
        Guid orderId,
        ReviewActionKind kind,
        string reviewer,
        string reason,
        string? reportReference,
        CancellationToken cancellationToken)
    {
        await using (await locks.AcquireAsync(orderId, cancellationToken))
        {
            var order = await GetOrderAsync(orderId, cancellationToken);
            order.Review(kind, reviewer, reason, reportReference, time.GetUtcNow());
            await orders.SaveAsync(order, [], cancellationToken);
        }

        // An approval lets the order buy and send; a release lets the refund go out.
        return await onRamp.ProcessAsync(orderId, cancellationToken);
    }
}
