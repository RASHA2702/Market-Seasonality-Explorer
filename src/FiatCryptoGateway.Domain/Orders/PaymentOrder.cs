using FiatCryptoGateway.Domain.Assets;
using FiatCryptoGateway.Domain.Compliance;
using FiatCryptoGateway.Domain.Pricing;

namespace FiatCryptoGateway.Domain.Orders;

public enum OrderStatus
{
    AwaitingFiatPayment,
    FiatReceived,
    ComplianceReview,
    ReadyToSettle,
    CryptoPurchased,
    CryptoSent,
    Completed,
    Expired,
    ComplianceRejected,
    RefundPending,
    Refunded,
    Failed,
}

public sealed record OrderEvent(int Sequence, OrderStatus? From, OrderStatus To, DateTimeOffset OccurredAt, string Reason);

public sealed record FiatPaymentConfirmation(
    string Provider,
    string ProviderPaymentId,
    Guid OrderId,
    Money Amount,
    DateTimeOffset CapturedAt);

public sealed record LiquidityExecution(
    string Provider,
    string ExecutionId,
    Guid OrderId,
    Money FiatSpent,
    Money CryptoReceived,
    DateTimeOffset ExecutedAt);

public sealed record CryptoTransfer(
    string Provider,
    string TransactionHash,
    Guid OrderId,
    Money Amount,
    string DestinationAddress,
    Money NetworkFeePaid,
    DateTimeOffset BroadcastAt);

public sealed record FiatRefund(string Provider, string RefundId, Guid OrderId, Money Amount, DateTimeOffset RefundedAt);

/// <summary>
/// An on-ramp order: the customer pays fiat and receives crypto. Every change goes through an explicit,
/// recorded transition; anything not in <see cref="AllowedTransitions"/> is refused.
/// Crypto is bought only after fiat is captured and compliance approves, and sent only after it is bought.
/// </summary>
public sealed class PaymentOrder
{
    private static readonly Dictionary<OrderStatus, OrderStatus[]> AllowedTransitions = new()
    {
        [OrderStatus.AwaitingFiatPayment] = [OrderStatus.FiatReceived, OrderStatus.RefundPending, OrderStatus.Expired],
        [OrderStatus.FiatReceived] = [OrderStatus.ReadyToSettle, OrderStatus.ComplianceReview, OrderStatus.ComplianceRejected],
        [OrderStatus.ComplianceReview] = [OrderStatus.ReadyToSettle, OrderStatus.ComplianceRejected],
        [OrderStatus.ReadyToSettle] = [OrderStatus.CryptoPurchased, OrderStatus.Failed],
        [OrderStatus.CryptoPurchased] = [OrderStatus.CryptoSent, OrderStatus.Failed],
        [OrderStatus.CryptoSent] = [OrderStatus.Completed, OrderStatus.Failed],
        [OrderStatus.Expired] = [OrderStatus.RefundPending],
        [OrderStatus.ComplianceRejected] = [OrderStatus.RefundPending],
        [OrderStatus.RefundPending] = [OrderStatus.Refunded],
    };

    private readonly List<OrderEvent> _events = [];

    private PaymentOrder(
        Guid orderId,
        string clientId,
        string customerId,
        Quote quote,
        string destinationAddress,
        DateTimeOffset createdAt,
        DateTimeOffset paymentDeadline)
    {
        OrderId = orderId;
        ClientId = clientId;
        CustomerId = customerId;
        Quote = quote;
        DestinationAddress = destinationAddress;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
        PaymentDeadline = paymentDeadline;
    }

    public Guid OrderId { get; }

    public string ClientId { get; }

    public string CustomerId { get; }

    public Quote Quote { get; }

    public string DestinationAddress { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>The locked price is honoured only for payments captured by this time.</summary>
    public DateTimeOffset PaymentDeadline { get; }

    public OrderStatus Status { get; private set; }

    public string? FiatSessionId { get; private set; }

    public Uri? CheckoutUrl { get; private set; }

    public FiatPaymentConfirmation? FiatPayment { get; private set; }

    public ComplianceDecision? Compliance { get; private set; }

    public LiquidityExecution? Purchase { get; private set; }

    public CryptoTransfer? Transfer { get; private set; }

    public FiatRefund? Refund { get; private set; }

    public int Confirmations { get; private set; }

    public IReadOnlyList<OrderEvent> Events => _events.AsReadOnly();

    public static bool IsAllowedTransition(OrderStatus from, OrderStatus to) =>
        AllowedTransitions.TryGetValue(from, out var allowed) && allowed.Contains(to);

    public static PaymentOrder Create(
        Guid orderId,
        string clientId,
        string customerId,
        Quote quote,
        string destinationAddress,
        DateTimeOffset now,
        TimeSpan paymentWindow)
    {
        ArgumentNullException.ThrowIfNull(quote);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(customerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationAddress);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(paymentWindow, TimeSpan.Zero);

        if (orderId == Guid.Empty)
        {
            throw new ArgumentException("An order ID is required.", nameof(orderId));
        }

        if (!string.Equals(quote.ClientId, clientId, StringComparison.Ordinal))
        {
            throw new GatewayRuleException("QUOTE_NOT_FOUND", "The quote does not exist.");
        }

        if (quote.IsExpiredAt(now))
        {
            throw new GatewayRuleException("QUOTE_EXPIRED", "The quote has expired; request a new one.");
        }

        var order = new PaymentOrder(orderId, clientId, customerId, quote, destinationAddress, now, now + paymentWindow);
        order.Append(null, OrderStatus.AwaitingFiatPayment, now, "ORDER_CREATED");
        return order;
    }

    public void AttachPaymentSession(string sessionId, Uri checkoutUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(checkoutUrl);
        if (FiatSessionId is not null)
        {
            throw new InvalidOperationException("A payment session is already attached.");
        }

        FiatSessionId = sessionId;
        CheckoutUrl = checkoutUrl;
    }

    /// <summary>
    /// Records captured fiat. A payment after the deadline, or for a different amount or currency, is
    /// not converted: the order moves to refund instead, because the locked price no longer applies.
    /// </summary>
    public void RecordFiatPayment(FiatPaymentConfirmation payment, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(payment);
        EnsureSameOrder(payment.OrderId);
        EnsureStatus(OrderStatus.AwaitingFiatPayment, OrderStatus.Expired);
        FiatPayment = payment;

        if (Status == OrderStatus.Expired || payment.CapturedAt > PaymentDeadline)
        {
            MoveTo(OrderStatus.RefundPending, now, "PAYMENT_AFTER_DEADLINE");
        }
        else if (payment.Amount != Quote.SourceAmount)
        {
            MoveTo(OrderStatus.RefundPending, now, "PAYMENT_AMOUNT_MISMATCH");
        }
        else
        {
            MoveTo(OrderStatus.FiatReceived, now, "FIAT_CAPTURED");
        }
    }

    public void ApplyCompliance(ComplianceDecision decision, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(decision);
        EnsureSameOrder(decision.OrderId);
        EnsureStatus(OrderStatus.FiatReceived, OrderStatus.ComplianceReview);
        Compliance = decision;

        switch (decision.Outcome)
        {
            case ComplianceOutcome.Approved:
                MoveTo(OrderStatus.ReadyToSettle, now, "COMPLIANCE_APPROVED");
                break;
            case ComplianceOutcome.Rejected:
                MoveTo(OrderStatus.ComplianceRejected, now, "COMPLIANCE_REJECTED");
                break;
            default:
                if (Status != OrderStatus.ComplianceReview)
                {
                    MoveTo(OrderStatus.ComplianceReview, now, "COMPLIANCE_REVIEW_REQUIRED");
                }

                break;
        }
    }

    public void RecordPurchase(LiquidityExecution execution, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(execution);
        EnsureSameOrder(execution.OrderId);
        EnsureStatus(OrderStatus.ReadyToSettle);

        if (execution.FiatSpent != Quote.NetSourceAmount)
        {
            throw new GatewayRuleException("EXECUTION_MISMATCH", "The liquidity provider spent a different fiat amount than quoted.");
        }

        if (execution.CryptoReceived.Asset != Quote.DestinationAmount.Asset || !execution.CryptoReceived.IsPositive)
        {
            throw new GatewayRuleException("EXECUTION_MISMATCH", "The liquidity provider delivered the wrong asset.");
        }

        Purchase = execution;
        MoveTo(OrderStatus.CryptoPurchased, now, "CRYPTO_PURCHASED");
    }

    public void RecordTransfer(CryptoTransfer transfer, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        EnsureSameOrder(transfer.OrderId);
        EnsureStatus(OrderStatus.CryptoPurchased);

        if (transfer.Amount != Quote.DestinationAmount ||
            !string.Equals(transfer.DestinationAddress, DestinationAddress, StringComparison.Ordinal))
        {
            throw new GatewayRuleException("TRANSFER_MISMATCH", "The transfer does not match the quoted amount and destination.");
        }

        if (transfer.NetworkFeePaid.Asset.Id != Quote.DestinationAmount.Asset.NetworkFeeAsset)
        {
            throw new GatewayRuleException("TRANSFER_MISMATCH", "The network fee must be paid in the network's fee asset.");
        }

        Transfer = transfer;
        MoveTo(OrderStatus.CryptoSent, now, "CRYPTO_BROADCAST");
    }

    public void RecordConfirmations(string transactionHash, int confirmations, int requiredConfirmations, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(confirmations);
        ArgumentOutOfRangeException.ThrowIfLessThan(requiredConfirmations, 1);
        EnsureStatus(OrderStatus.CryptoSent);

        if (Transfer is null || !string.Equals(Transfer.TransactionHash, transactionHash, StringComparison.Ordinal))
        {
            throw new GatewayRuleException("TRANSACTION_MISMATCH", "The confirmation is for a different transaction.");
        }

        Confirmations = Math.Max(Confirmations, confirmations);
        if (Confirmations >= requiredConfirmations)
        {
            MoveTo(OrderStatus.Completed, now, "CHAIN_CONFIRMED");
        }
    }

    /// <summary>Returns true when the order expired because its payment window elapsed without payment.</summary>
    public bool ExpireIfOverdue(DateTimeOffset now)
    {
        if (Status != OrderStatus.AwaitingFiatPayment || now <= PaymentDeadline)
        {
            return false;
        }

        MoveTo(OrderStatus.Expired, now, "PAYMENT_WINDOW_ELAPSED");
        return true;
    }

    /// <summary>
    /// A compliance-rejected payment is held until a person decides to refund it (some rejections must
    /// be reported to authorities instead). This records that decision.
    /// </summary>
    public void ApproveRefundAfterRejection(string approvedBy, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(approvedBy);
        EnsureStatus(OrderStatus.ComplianceRejected);
        MoveTo(OrderStatus.RefundPending, now, $"REFUND_APPROVED_BY:{approvedBy}");
    }

    public void RecordRefund(FiatRefund refund, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(refund);
        EnsureSameOrder(refund.OrderId);
        EnsureStatus(OrderStatus.RefundPending);

        if (FiatPayment is null || refund.Amount != FiatPayment.Amount)
        {
            throw new GatewayRuleException("REFUND_MISMATCH", "A refund must return exactly what was paid.");
        }

        Refund = refund;
        MoveTo(OrderStatus.Refunded, now, "FIAT_REFUNDED");
    }

    public void MarkFailed(string reason, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        MoveTo(OrderStatus.Failed, now, reason);
    }

    private void MoveTo(OrderStatus next, DateTimeOffset now, string reason)
    {
        if (!IsAllowedTransition(Status, next))
        {
            throw new GatewayRuleException("INVALID_ORDER_TRANSITION", $"An order cannot move from {Status} to {next}.");
        }

        Append(Status, next, now, reason);
    }

    private void Append(OrderStatus? from, OrderStatus to, DateTimeOffset now, string reason)
    {
        _events.Add(new OrderEvent(_events.Count + 1, from, to, now, reason));
        Status = to;
        UpdatedAt = now;
    }

    private void EnsureSameOrder(Guid orderId)
    {
        if (orderId != OrderId)
        {
            throw new GatewayRuleException("ORDER_MISMATCH", "The event belongs to a different order.");
        }
    }

    private void EnsureStatus(params ReadOnlySpan<OrderStatus> expected)
    {
        foreach (var status in expected)
        {
            if (status == Status)
            {
                return;
            }
        }

        throw new GatewayRuleException("ORDER_STATE_CONFLICT", $"The order is {Status}; this action is not possible now.");
    }
}
