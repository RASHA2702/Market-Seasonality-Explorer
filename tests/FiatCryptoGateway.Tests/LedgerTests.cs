using FiatCryptoGateway.Domain;
using FiatCryptoGateway.Domain.Assets;
using FiatCryptoGateway.Domain.Ledger;
using FiatCryptoGateway.Domain.Orders;

namespace FiatCryptoGateway.Tests;

public sealed class LedgerTests
{
    private static readonly DateTimeOffset At = Fixtures.Start;

    [Fact]
    public void UnbalancedEntry_IsRefused()
    {
        var builder = new JournalEntryBuilder()
            .Debit(LedgerAccount.PspClearing("psp", Fixtures.Inr.Id), new Money(100m, Fixtures.Inr))
            .Credit(LedgerAccount.CustomerOrders(Fixtures.Inr.Id), new Money(99m, Fixtures.Inr));

        var exception = Assert.Throws<GatewayRuleException>(() => builder.Build("TEST", null, At));

        Assert.Equal("UNBALANCED_ENTRY", exception.Code);
    }

    [Fact]
    public void EntryBalancedOnlyAcrossCurrencies_IsRefused()
    {
        // 100 INR against 100 USDC adds up numerically but is meaningless; each asset must balance.
        var builder = new JournalEntryBuilder()
            .Debit(LedgerAccount.PspClearing("psp", Fixtures.Inr.Id), new Money(100m, Fixtures.Inr))
            .Credit(LedgerAccount.CustomerOrders(Fixtures.Usdc.Id), new Money(100m, Fixtures.Usdc));

        var exception = Assert.Throws<GatewayRuleException>(() => builder.Build("TEST", null, At));

        Assert.Equal("UNBALANCED_ENTRY", exception.Code);
    }

    [Fact]
    public void LineInTheWrongAssetForItsAccount_IsRefused()
    {
        var builder = new JournalEntryBuilder()
            .Debit(LedgerAccount.PspClearing("psp", Fixtures.Usd.Id), new Money(100m, Fixtures.Inr))
            .Credit(LedgerAccount.CustomerOrders(Fixtures.Inr.Id), new Money(100m, Fixtures.Inr));

        var exception = Assert.Throws<GatewayRuleException>(() => builder.Build("TEST", null, At));

        Assert.Equal("ACCOUNT_ASSET_MISMATCH", exception.Code);
    }

    [Fact]
    public void Builder_SkipsZeroAndFlipsNegativeAmounts()
    {
        var entry = new JournalEntryBuilder()
            .Debit(LedgerAccount.PspClearing("psp", Fixtures.Inr.Id), new Money(100m, Fixtures.Inr))
            .Credit(LedgerAccount.ServiceFees(Fixtures.Inr.Id), Money.Zero(Fixtures.Inr))
            .Debit(LedgerAccount.CustomerOrders(Fixtures.Inr.Id), new Money(-100m, Fixtures.Inr))
            .Build("TEST", null, At);

        Assert.Equal(2, entry.Lines.Count);
        Assert.Equal(EntrySide.Credit, entry.Lines[1].Side);
        Assert.Equal(100m, entry.Lines[1].Amount.Amount);
    }

    [Fact]
    public void CompletedOrder_BalancesEveryAssetAndClearsTheCustomerLiability()
    {
        var order = Fixtures.Order();
        var payment = Fixtures.Payment(order);
        var execution = Fixtures.Execution(order);
        var transfer = Fixtures.Transfer(order);
        order.RecordFiatPayment(payment, At);
        order.ApplyCompliance(Fixtures.Decision(order), At);
        order.RecordPurchase(execution, At);
        order.RecordTransfer(transfer, At);

        var entries = new[]
        {
            OnRampPostings.FiatReceived(order, payment, At),
            OnRampPostings.CryptoPurchased(order, execution, "custody", At),
            OnRampPostings.CryptoSent(order, transfer, "custody", At),
        };
        var balances = LedgerBalances.Compute(entries);

        foreach (var asset in balances.GroupBy(balance => balance.Asset))
        {
            Assert.Equal(0m, asset.Sum(balance => balance.DebitMinusCredit));
        }

        Assert.Equal(0m, Balance(balances, "liabilities:customer_orders", Fixtures.Inr.Id));
        Assert.Equal(0m, Balance(balances, "liabilities:customer_orders", Fixtures.Usdc.Id));
        Assert.Equal(-150m, Balance(balances, "revenue:service_fees", Fixtures.Inr.Id));
        Assert.Equal(-0.58115m, Balance(balances, "revenue:spread", Fixtures.Usdc.Id));            // 116.23 - 115.64885
        Assert.Equal(-1.5m, Balance(balances, "revenue:network_fee_recovery", Fixtures.Usdc.Id));
        Assert.Equal(0.0004m, Balance(balances, "expenses:network_fees", Fixtures.Ether.Id));      // gas, paid in ETH
        Assert.Equal(-9_850m, Balance(balances, "liabilities:liquidity_provider:test-lp", Fixtures.Inr.Id));
    }

    [Fact]
    public void WorseExecutionThanQuoted_IsRecordedAsASpreadLoss()
    {
        var order = Fixtures.Order();
        order.RecordFiatPayment(Fixtures.Payment(order), At);
        order.ApplyCompliance(Fixtures.Decision(order), At);
        var execution = Fixtures.Execution(order) with { CryptoReceived = new Money(115m, Fixtures.Usdc) };
        order.RecordPurchase(execution, At);

        var balances = LedgerBalances.Compute([OnRampPostings.CryptoPurchased(order, execution, "custody", At)]);

        Assert.Equal(0.64885m, Balance(balances, "revenue:spread", Fixtures.Usdc.Id)); // a debit: the gateway absorbed it
        Assert.Equal(-115.64885m, Balance(balances, "liabilities:customer_orders", Fixtures.Usdc.Id));
    }

    [Fact]
    public void Refund_ReturnsTheCustomerLiabilityToZero()
    {
        var order = Fixtures.Order();
        var payment = Fixtures.Payment(order, capturedAt: order.PaymentDeadline.AddMinutes(1));
        order.RecordFiatPayment(payment, At);
        var refund = new FiatRefund("test-psp", "rfd_1", order.OrderId, payment.Amount, At);
        order.RecordRefund(refund, At);

        var balances = LedgerBalances.Compute([OnRampPostings.FiatReceived(order, payment, At), OnRampPostings.Refunded(order, refund, At)]);

        Assert.All(balances, balance => Assert.Equal(0m, balance.DebitMinusCredit));
        Assert.Equal(OrderStatus.Refunded, order.Status);
    }

    [Fact]
    public void EachPosting_RecordsTheProviderMovementItAccountsFor()
    {
        var order = Fixtures.Order();
        var payment = Fixtures.Payment(order);
        var execution = Fixtures.Execution(order);
        var transfer = Fixtures.Transfer(order);
        var refund = new FiatRefund("test-psp", "rfd_1", order.OrderId, payment.Amount, At.AddMinutes(9));

        var received = OnRampPostings.FiatReceived(order, payment, At);
        var purchased = OnRampPostings.CryptoPurchased(order, execution, "custody", At);
        var sent = OnRampPostings.CryptoSent(order, transfer, "custody", At);
        var refunded = OnRampPostings.Refunded(order, refund, At);

        Assert.Equal(new ExternalReference("test-psp", "pay_1", payment.Amount, payment.CapturedAt), received.External);
        Assert.Equal(new ExternalReference("test-lp", "lpx_1", execution.CryptoReceived, execution.ExecutedAt), purchased.External);
        Assert.Equal(new ExternalReference("test-custody", "tx_1", transfer.Amount, transfer.BroadcastAt), sent.External);
        Assert.Equal(new ExternalReference("test-psp", "rfd_1", refund.Amount, refund.RefundedAt), refunded.External);

        // The provider amount is what moved through the provider's account in the books.
        Assert.Equal(payment.Amount, Line(received, "assets:psp_clearing:test-psp", EntrySide.Debit));
        Assert.Equal(execution.CryptoReceived, Line(purchased, "assets:hot_wallet:custody", EntrySide.Debit));
        Assert.Equal(transfer.Amount, Line(sent, "assets:hot_wallet:custody", EntrySide.Credit, Fixtures.Usdc.Id));
        Assert.Equal(refund.Amount, Line(refunded, "assets:psp_clearing:test-psp", EntrySide.Credit));
    }

    [Theory]
    [InlineData("", "pay_1", 100)]
    [InlineData("test-psp", " ", 100)]
    [InlineData("test-psp", "pay_1", 0)]
    public void IncompleteProviderReference_IsRefused(string provider, string id, int amount)
    {
        var builder = new JournalEntryBuilder()
            .Debit(LedgerAccount.PspClearing("test-psp", Fixtures.Inr.Id), new Money(100m, Fixtures.Inr))
            .Credit(LedgerAccount.CustomerOrders(Fixtures.Inr.Id), new Money(100m, Fixtures.Inr));

        var exception = Assert.Throws<GatewayRuleException>(() =>
            builder.Build("FIAT_RECEIVED", null, At, new ExternalReference(provider, id, new Money(amount, Fixtures.Inr), At)));

        Assert.Equal("INVALID_EXTERNAL_REFERENCE", exception.Code);
    }

    private static Money Line(JournalEntry entry, string account, EntrySide side, AssetId? asset = null) =>
        entry.Lines.Single(line => line.Account.Code == account && line.Side == side && (asset is null || line.Account.Asset == asset)).Amount;

    private static decimal Balance(IReadOnlyList<AccountBalance> balances, string account, AssetId asset) =>
        balances.Single(balance => balance.Account == account && balance.Asset == asset).DebitMinusCredit;
}
