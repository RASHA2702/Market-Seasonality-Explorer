using FiatCryptoGateway.Domain;
using FiatCryptoGateway.Domain.Assets;
using FiatCryptoGateway.Domain.Compliance;
using FiatCryptoGateway.Domain.Orders;

namespace FiatCryptoGateway.Tests;

public sealed class PaymentOrderTests
{
    private static readonly DateTimeOffset Later = Fixtures.Start.AddMinutes(5);

    [Fact]
    public void HappyPath_RecordsEveryTransitionInOrder()
    {
        var order = Fixtures.Order();

        order.RecordFiatPayment(Fixtures.Payment(order), Later);
        order.ApplyCompliance(Fixtures.Decision(order), Later);
        order.RecordPurchase(Fixtures.Execution(order), Later);
        order.RecordTransfer(Fixtures.Transfer(order), Later);
        order.RecordConfirmations("tx_1", 12, requiredConfirmations: 12, Later);

        Assert.Equal(OrderStatus.Completed, order.Status);
        Assert.Equal(
            ["ORDER_CREATED", "FIAT_CAPTURED", "COMPLIANCE_APPROVED", "CRYPTO_PURCHASED", "CRYPTO_BROADCAST", "CHAIN_CONFIRMED"],
            order.Events.Select(e => e.Reason));
        Assert.Equal([1, 2, 3, 4, 5, 6], order.Events.Select(e => e.Sequence));
    }

    [Fact]
    public void PaymentAfterTheDeadline_IsRefundedNotConverted()
    {
        var order = Fixtures.Order();

        order.RecordFiatPayment(Fixtures.Payment(order, capturedAt: order.PaymentDeadline.AddSeconds(1)), Later);

        Assert.Equal(OrderStatus.RefundPending, order.Status);
        Assert.Equal("PAYMENT_AFTER_DEADLINE", order.Events[^1].Reason);
    }

    [Fact]
    public void WrongAmount_IsRefundedNotConverted()
    {
        var order = Fixtures.Order();

        order.RecordFiatPayment(Fixtures.Payment(order, new Money(9_999.99m, Fixtures.Inr)), Later);

        Assert.Equal(OrderStatus.RefundPending, order.Status);
        Assert.Equal("PAYMENT_AMOUNT_MISMATCH", order.Events[^1].Reason);
    }

    [Fact]
    public void WrongCurrency_IsRefundedNotConverted()
    {
        var order = Fixtures.Order();

        order.RecordFiatPayment(Fixtures.Payment(order, new Money(10_000m, Fixtures.Usd)), Later);

        Assert.Equal(OrderStatus.RefundPending, order.Status);
    }

    [Fact]
    public void PaymentForAnExpiredOrder_IsRefunded()
    {
        var order = Fixtures.Order();
        Assert.True(order.ExpireIfOverdue(order.PaymentDeadline.AddSeconds(1)));

        order.RecordFiatPayment(Fixtures.Payment(order), Later);

        Assert.Equal(OrderStatus.RefundPending, order.Status);
    }

    [Fact]
    public void Order_DoesNotExpireBeforeItsDeadline()
    {
        var order = Fixtures.Order();

        Assert.False(order.ExpireIfOverdue(order.PaymentDeadline));
        Assert.Equal(OrderStatus.AwaitingFiatPayment, order.Status);
    }

    [Fact]
    public void CryptoCannotBeBoughtBeforeComplianceApproval()
    {
        var order = Fixtures.Order();
        order.RecordFiatPayment(Fixtures.Payment(order), Later);

        var exception = Assert.Throws<GatewayRuleException>(() => order.RecordPurchase(Fixtures.Execution(order), Later));

        Assert.Equal("ORDER_STATE_CONFLICT", exception.Code);
    }

    [Fact]
    public void CryptoCannotBeSentBeforeItIsBought()
    {
        var order = Fixtures.Order();
        order.RecordFiatPayment(Fixtures.Payment(order), Later);
        order.ApplyCompliance(Fixtures.Decision(order), Later);

        var exception = Assert.Throws<GatewayRuleException>(() => order.RecordTransfer(Fixtures.Transfer(order), Later));

        Assert.Equal("ORDER_STATE_CONFLICT", exception.Code);
    }

    [Fact]
    public void ComplianceDecisionForAnotherOrder_IsRefused()
    {
        var order = Fixtures.Order();
        var other = Fixtures.Order();
        order.RecordFiatPayment(Fixtures.Payment(order), Later);

        var exception = Assert.Throws<GatewayRuleException>(() => order.ApplyCompliance(Fixtures.Decision(other), Later));

        Assert.Equal("ORDER_MISMATCH", exception.Code);
    }

    [Theory]
    [InlineData(ComplianceOutcome.ManualReview, OrderStatus.ComplianceReview)]
    [InlineData(ComplianceOutcome.Rejected, OrderStatus.ComplianceRejected)]
    public void UnapprovedCompliance_StopsTheOrder(ComplianceOutcome outcome, OrderStatus expected)
    {
        var order = Fixtures.Order();
        order.RecordFiatPayment(Fixtures.Payment(order), Later);

        order.ApplyCompliance(Fixtures.Decision(order, outcome), Later);

        Assert.Equal(expected, order.Status);
    }

    [Fact]
    public void TransferToAnotherAddress_IsRefused()
    {
        var order = ReadyToSend();

        var exception = Assert.Throws<GatewayRuleException>(() =>
            order.RecordTransfer(Fixtures.Transfer(order, "0x2222222222222222222222222222222222222222"), Later));

        Assert.Equal("TRANSFER_MISMATCH", exception.Code);
    }

    [Fact]
    public void ExecutionForADifferentFiatAmount_IsRefused()
    {
        var order = Fixtures.Order();
        order.RecordFiatPayment(Fixtures.Payment(order), Later);
        order.ApplyCompliance(Fixtures.Decision(order), Later);
        var execution = Fixtures.Execution(order) with { FiatSpent = new Money(10_000m, Fixtures.Inr) };

        var exception = Assert.Throws<GatewayRuleException>(() => order.RecordPurchase(execution, Later));

        Assert.Equal("EXECUTION_MISMATCH", exception.Code);
    }

    [Fact]
    public void TooFewConfirmations_DoNotComplete()
    {
        var order = ReadyToSend();
        order.RecordTransfer(Fixtures.Transfer(order), Later);

        order.RecordConfirmations("tx_1", 11, requiredConfirmations: 12, Later);

        Assert.Equal(OrderStatus.CryptoSent, order.Status);
        Assert.Equal(11, order.Confirmations);
    }

    [Fact]
    public void ExpiredQuote_CannotBecomeAnOrder()
    {
        var exception = Assert.Throws<GatewayRuleException>(() => PaymentOrder.Create(
            Guid.NewGuid(), Fixtures.ClientId, "cust_verified", Fixtures.Quote(), Fixtures.Address, Fixtures.Start.AddMinutes(2), TimeSpan.FromMinutes(15)));

        Assert.Equal("QUOTE_EXPIRED", exception.Code);
    }

    [Fact]
    public void AnotherClientsQuote_IsNotFound()
    {
        var exception = Assert.Throws<GatewayRuleException>(() => PaymentOrder.Create(
            Guid.NewGuid(), "client-b", "cust_verified", Fixtures.Quote(), Fixtures.Address, Fixtures.Start, TimeSpan.FromMinutes(15)));

        Assert.Equal("QUOTE_NOT_FOUND", exception.Code);
    }

    [Theory]
    [InlineData(OrderStatus.Completed)]
    [InlineData(OrderStatus.Refunded)]
    [InlineData(OrderStatus.Failed)]
    public void TerminalStates_HaveNoExits(OrderStatus terminal)
    {
        Assert.DoesNotContain(Enum.GetValues<OrderStatus>(), next => PaymentOrder.IsAllowedTransition(terminal, next));
    }

    private static PaymentOrder ReadyToSend()
    {
        var order = Fixtures.Order();
        order.RecordFiatPayment(Fixtures.Payment(order), Later);
        order.ApplyCompliance(Fixtures.Decision(order), Later);
        order.RecordPurchase(Fixtures.Execution(order), Later);
        return order;
    }
}
