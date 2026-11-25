using FiatCryptoGateway.Domain.Orders;

namespace FiatCryptoGateway.Application.OnRamp;

public enum CustomerPaymentStatus
{
    AwaitingPayment,
    Expired,
    PaymentReceived,
    RefundStarted,
    Refunded,
}

/// <summary>
/// What the paying customer may see of an order: whether it is waiting for payment, paid, being refunded or expired.
/// Review, rejection, freezing, the merchant's settlement and processing failures all appear as "payment received",
/// so nothing a customer sees reveals a compliance step (which could tip off someone under suspicion).
/// </summary>
public static class CustomerView
{
    public static CustomerPaymentStatus Status(OrderStatus status) => status switch
    {
        OrderStatus.AwaitingFiatPayment => CustomerPaymentStatus.AwaitingPayment,
        OrderStatus.Expired => CustomerPaymentStatus.Expired,
        OrderStatus.RefundPending => CustomerPaymentStatus.RefundStarted,
        OrderStatus.Refunded => CustomerPaymentStatus.Refunded,
        OrderStatus.FiatReceived or OrderStatus.ComplianceReview or OrderStatus.ComplianceRejected or OrderStatus.Frozen
            or OrderStatus.ReadyToSettle or OrderStatus.CryptoPurchased or OrderStatus.CryptoSent or OrderStatus.Completed
            or OrderStatus.Failed => CustomerPaymentStatus.PaymentReceived,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Every order status needs a customer view."),
    };

    /// <summary>The status as of <paramref name="now"/>: an unpaid order past its deadline is shown as expired at once.</summary>
    public static CustomerPaymentStatus Status(PaymentOrder order, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(order);
        var status = Status(order.Status);
        return status == CustomerPaymentStatus.AwaitingPayment && now > order.PaymentDeadline ? CustomerPaymentStatus.Expired : status;
    }
}
