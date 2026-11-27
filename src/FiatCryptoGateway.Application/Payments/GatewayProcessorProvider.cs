using FiatCryptoGateway.Application.Ports;
using FiatCryptoGateway.Application.Reconciliation;
using FiatCryptoGateway.Domain.Assets;
using FiatCryptoGateway.Domain.Ledger;
using FiatCryptoGateway.Domain.Orders;
using FiatCryptoGateway.Domain.Payments;

namespace FiatCryptoGateway.Application.Payments;

/// <summary>
/// The gateway's own processor, seen by the order flow as its payment provider. It makes no checkout session of its own
/// (customers pay on the gateway's hosted page), refunds the payment the processor captured, and publishes a settlement
/// report from the processor's own records, which reconciliation compares with the gateway's books.
/// </summary>
/// <remarks>
/// It takes no payment lock. Refunds are asked for while the order's own lock is held, and the processor takes the
/// payment lock first and the order lock only after letting it go; taking the payment lock here could make the two
/// wait on each other. A refund needs none: its identifier comes from the attempt, so repeating it is the same refund.
/// </remarks>
public sealed class GatewayProcessorProvider(IPaymentAttemptStore attempts, PaymentRailSet rails) : IFiatPaymentProvider, ISettlementReportSource
{
    public const string ProviderName = "gateway-processor";

    public string Name => ProviderName;

    public string Provider => ProviderName;

    public Task<FiatPaymentSession> CreateSessionAsync(PaymentOrder order, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);
        var sessionId = $"gps_{order.OrderId:N}";
        return Task.FromResult(new FiatPaymentSession(sessionId, new Uri($"https://pay.gateway.invalid/{sessionId}")));
    }

    /// <summary>Refunds the whole payment the processor captured for the order. Repeating it returns the same refund.</summary>
    public async Task<FiatRefund> RefundAsync(PaymentOrder order, Money amount, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(amount);
        var attempt = await attempts.FindCapturedForOrderAsync(order.OrderId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Order {order.OrderId} has no payment taken by the gateway's processor, so there is nothing to refund here.");
        if (amount != attempt.Amount)
        {
            throw new ArgumentException("The processor refunds the whole payment only.", nameof(amount));
        }

        if (attempt.Status == PaymentAttemptStatus.Refunded)
        {
            return ToRefund(attempt);
        }

        var refundId = PaymentIdentifiers.Refund(attempt.AttemptId);
        var refundedAt = await rails.Require(attempt.Method).RefundAsync(attempt.RailReference!, attempt.Amount, refundId, cancellationToken);
        attempt.Refund(refundId, refundedAt);
        try
        {
            await attempts.SaveAsync(attempt, cancellationToken);
        }
        catch (PaymentAttemptConcurrencyException)
        {
            // Another refund of this payment saved first. Both used the same identifier, so it is the same refund.
            var current = await attempts.FindAsync(attempt.AttemptId, cancellationToken);
            if (current is { Status: PaymentAttemptStatus.Refunded })
            {
                return ToRefund(current);
            }

            throw;
        }

        return ToRefund(attempt);
    }

    public async Task<SettlementReport> GetReportAsync(DateOnly day, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken)
    {
        var lines = new List<SettlementLine>();
        foreach (var attempt in await attempts.ListSettledBetweenAsync(windowStart, windowEnd, cancellationToken))
        {
            if (attempt.CapturedAt is { } capturedAt && capturedAt >= windowStart && capturedAt < windowEnd)
            {
                lines.Add(new SettlementLine(EntryKinds.FiatReceived, attempt.ProviderPaymentId!, attempt.OrderId, attempt.Amount, capturedAt));
            }

            if (attempt.RefundedAt is { } refundedAt && refundedAt >= windowStart && refundedAt < windowEnd)
            {
                lines.Add(new SettlementLine(EntryKinds.FiatRefunded, attempt.RefundId!, attempt.OrderId, attempt.Amount, refundedAt));
            }
        }

        return new SettlementReport(
            ProviderName,
            day,
            [.. lines.OrderBy(line => line.OccurredAt).ThenBy(line => line.Reference, StringComparer.Ordinal)]);
    }

    private static FiatRefund ToRefund(PaymentAttempt attempt) =>
        new(ProviderName, attempt.RefundId!, attempt.OrderId, attempt.Amount, attempt.RefundedAt!.Value);
}
