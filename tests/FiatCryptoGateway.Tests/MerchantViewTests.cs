using FiatCryptoGateway.Application.OnRamp;
using FiatCryptoGateway.Domain.Compliance;
using FiatCryptoGateway.Domain.Orders;

namespace FiatCryptoGateway.Tests;

public sealed class MerchantViewTests
{
    private static readonly DateTimeOffset Later = Fixtures.Start.AddMinutes(5);

    [Fact]
    public void ReviewRejectionAndFreeze_AppearAsOneOnHoldStep()
    {
        var order = Fixtures.Order();
        order.RecordFiatPayment(Fixtures.Payment(order), Later);
        order.ApplyCompliance(Fixtures.Decision(order, ComplianceOutcome.ManualReview), Later);
        order.Review(ReviewActionKind.Reject, "officer", "Suspicious.", null, Later);
        order.Review(ReviewActionKind.FreezeAndReport, "officer", "Reported.", "STR-1", Later);
        order.Review(ReviewActionKind.ReleaseForRefund, "officer", "Release instructed.", null, Later);
        order.RecordRefund(new FiatRefund("test-psp", "rfd_1", order.OrderId, order.FiatPayment!.Amount, Later), Later);

        var events = MerchantView.Events(order);

        Assert.Equal(
            [
                (null, "AwaitingFiatPayment", "ORDER_CREATED"),
                ("AwaitingFiatPayment", "FiatReceived", "FIAT_CAPTURED"),
                ("FiatReceived", "OnHold", "ON_HOLD"),
                ("OnHold", "RefundPending", "REFUND_STARTED"),
                ("RefundPending", "Refunded", "FIAT_REFUNDED"),
            ],
            events.Select(e => (e.From, e.To, e.Reason)));
        Assert.Equal("Refunded", MerchantView.Status(order.Status));
    }

    [Theory]
    [InlineData(OrderStatus.ComplianceReview)]
    [InlineData(OrderStatus.ComplianceRejected)]
    [InlineData(OrderStatus.Frozen)]
    public void ComplianceStates_AreShownAsOnHold(OrderStatus status)
    {
        Assert.Equal("OnHold", MerchantView.Status(status));
    }
}
