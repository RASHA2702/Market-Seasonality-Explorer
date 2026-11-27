using FiatCryptoGateway.Application.Compliance;
using FiatCryptoGateway.Application.Payments;
using FiatCryptoGateway.Application.Reconciliation;
using FiatCryptoGateway.Domain.Assets;
using FiatCryptoGateway.Domain.Ledger;
using FiatCryptoGateway.Domain.Orders;
using FiatCryptoGateway.Domain.Payments;
using FiatCryptoGateway.Sandbox;
using Microsoft.Extensions.DependencyInjection;

namespace FiatCryptoGateway.Tests;

/// <summary>The processor as the order flow's payment provider: refunds, and the daily report reconciliation checks.</summary>
public sealed class GatewayProcessorProviderTests : IDisposable
{
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly DateOnly Day = new(2026, 11, 24); // the harness clock starts at 17:30 IST on this day

    private readonly ProcessorHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task ARefund_GoesThroughTheNetwork_AndRepeatingItIsTheSameRefund()
    {
        var order = await _h.CreateOrderAsync();
        var paid = await _h.StartCardAsync(order, "4111 1111 1111 1111");
        var amount = order.Quote.SourceAmount;

        var first = await _h.Provider.RefundAsync(order, amount, None);
        var second = await _h.Provider.RefundAsync(order, amount, None);

        Assert.Equal(first, second);
        Assert.Equal(
            (GatewayProcessorProvider.ProviderName, PaymentIdentifiers.Refund(paid.AttemptId), order.OrderId, amount),
            (first.Provider, first.RefundId, first.OrderId, first.Amount));
        var stored = await _h.StoredAsync(paid);
        Assert.Equal((PaymentAttemptStatus.Refunded, first.RefundId, first.RefundedAt), (stored.Status, stored.RefundId, stored.RefundedAt));
    }

    [Fact]
    public async Task RejectedMoney_IsRefundedThroughTheProcessor_OnlyAfterTheReview()
    {
        // A held order paid through the processor, rejected, reported, then released for refund.
        var order = await _h.CreateOrderAsync(6_000m, "USD");
        var paid = await _h.Processor.PayWithTestUpiAsync(order.OrderId, None);
        Assert.True(paid.IsPaid);
        Assert.Equal(OrderStatus.ComplianceReview, (await _h.Orders.FindAsync(order.OrderId, None))!.Status);
        var reviews = _h.Services.GetRequiredService<ComplianceReviewService>();

        await reviews.ReviewAsync(order.OrderId, ReviewActionKind.Reject, "analyst-1", "Source of funds not explained.", null, None);
        await reviews.ReviewAsync(order.OrderId, ReviewActionKind.FreezeAndReport, "analyst-1", "Reported as suspicious.", "STR-2026-0001", None);
        Assert.Equal(PaymentAttemptStatus.Captured, (await _h.StoredAsync(paid)).Status); // frozen money is not refunded

        await reviews.ReviewAsync(order.OrderId, ReviewActionKind.ReleaseForRefund, "analyst-1", "Release instructed in writing.", null, None);

        Assert.Equal(OrderStatus.Refunded, (await _h.Orders.FindAsync(order.OrderId, None))!.Status);
        var stored = await _h.StoredAsync(paid);
        Assert.Equal(PaymentAttemptStatus.Refunded, stored.Status);
        var entries = await _h.Ledger.ListAsync(order.OrderId, None);
        var refund = Assert.Single(entries, entry => entry.Kind == EntryKinds.FiatRefunded);
        Assert.Equal((GatewayProcessorProvider.ProviderName, stored.RefundId), (refund.External!.Provider, refund.External.Id));
        Assert.All(LedgerBalances.Compute(entries), balance => Assert.Equal(0m, balance.DebitMinusCredit));
    }

    [Fact]
    public async Task ARefundOfAnOrderNotPaidHere_OrForAnotherAmount_IsRefused()
    {
        var unpaid = await _h.CreateOrderAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => _h.Provider.RefundAsync(unpaid, unpaid.Quote.SourceAmount, None));

        var order = await _h.CreateOrderAsync();
        var paid = await _h.StartCardAsync(order, "4111 1111 1111 1111");
        var half = new Money(order.Quote.SourceAmount.Amount / 2, order.Quote.SourceAmount.Asset);

        await Assert.ThrowsAsync<ArgumentException>(() => _h.Provider.RefundAsync(order, half, None));

        Assert.Equal(PaymentAttemptStatus.Captured, (await _h.StoredAsync(paid)).Status);
    }

    [Fact]
    public async Task TheOrdersSession_PointsAtNoThirdPartyPage()
    {
        var order = await _h.CreateOrderAsync();

        Assert.Equal($"gps_{order.OrderId:N}", order.FiatSessionId);
        Assert.Equal("pay.gateway.invalid", order.CheckoutUrl!.Host); // .invalid never resolves
        Assert.Equal(GatewayProcessorProvider.ProviderName, _h.Provider.Name);
        Assert.Equal(GatewayProcessorProvider.ProviderName, _h.Provider.Provider);
    }

    [Fact]
    public async Task TheDailyReport_MatchesTheBooks_AndFlagsAPaymentTheBooksDoNotHaveYet()
    {
        var recorded = await _h.CreateOrderAsync();
        await _h.StartCardAsync(recorded, "4111 1111 1111 1111");
        var unrecorded = await _h.CreateOrderAsync();
        await _h.SeedCapturedAsync(unrecorded); // the processor took the money, but the order does not know yet

        _h.Time.Advance(TimeSpan.FromHours(13)); // 06:30 IST the next day
        var reconciliation = _h.Services.GetRequiredService<ReconciliationService>();
        var run = Assert.Single(await reconciliation.RunAsync(Day, None));

        Assert.Equal((GatewayProcessorProvider.ProviderName, 2, 1, 1), (run.Provider, run.ReportLines, run.LedgerEntries, run.Matched));
        var discrepancy = Assert.Single(run.Discrepancies);
        Assert.Equal(
            (DiscrepancyType.MissingInLedger, EntryKinds.FiatReceived, (Guid?)unrecorded.OrderId),
            (discrepancy.Type, discrepancy.Kind, discrepancy.OrderId));

        // Once the sweep has recorded it, the same day reconciles cleanly.
        await _h.Processor.SweepAsync(None);
        run = Assert.Single(await reconciliation.RunAsync(Day, None));
        Assert.Equal((2, 2, 2), (run.ReportLines, run.LedgerEntries, run.Matched));
        Assert.Empty(run.Discrepancies);
    }

    [Fact]
    public async Task TheDailyReport_PutsAPaymentAndItsLaterRefund_OnTheirOwnDays()
    {
        var dayStart = new DateTimeOffset(2026, 11, 23, 18, 30, 0, TimeSpan.Zero); // midnight IST
        var order = await _h.CreateOrderAsync();
        var paid = await _h.StartCardAsync(order, "4111 1111 1111 1111"); // 17:30 IST on the 24th
        _h.Time.Advance(TimeSpan.FromHours(13)); // 06:30 IST on the 25th
        await _h.Provider.RefundAsync(order, order.Quote.SourceAmount, None);

        var first = await _h.Provider.GetReportAsync(Day, dayStart, dayStart.AddDays(1), None);
        var second = await _h.Provider.GetReportAsync(Day.AddDays(1), dayStart.AddDays(1), dayStart.AddDays(2), None);

        Assert.Equal([(EntryKinds.FiatReceived, PaymentIdentifiers.Payment(paid.AttemptId))], first.Lines.Select(line => (line.Kind, line.Reference)));
        Assert.Equal([(EntryKinds.FiatRefunded, PaymentIdentifiers.Refund(paid.AttemptId))], second.Lines.Select(line => (line.Kind, line.Reference)));
    }

    [Fact]
    public async Task TheDailyReport_IncludesRefunds_OnTheDayTheyHappened()
    {
        var order = await _h.CreateOrderAsync();
        var paid = await _h.StartCardAsync(order, "4111 1111 1111 1111");
        await _h.Provider.RefundAsync(order, order.Quote.SourceAmount, None);

        var report = await _h.Provider.GetReportAsync(
            Day, new DateTimeOffset(2026, 11, 23, 18, 30, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 24, 18, 30, 0, TimeSpan.Zero), None);

        Assert.Equal(
            [(EntryKinds.FiatReceived, PaymentIdentifiers.Payment(paid.AttemptId)), (EntryKinds.FiatRefunded, PaymentIdentifiers.Refund(paid.AttemptId))],
            report.Lines.Select(line => (line.Kind, line.Reference)));
        Assert.All(report.Lines, line => Assert.Equal(order.OrderId, line.OrderId));

        var nextDay = await _h.Provider.GetReportAsync(
            Day.AddDays(1), new DateTimeOffset(2026, 11, 24, 18, 30, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 25, 18, 30, 0, TimeSpan.Zero), None);
        Assert.Empty(nextDay.Lines);
    }
}
