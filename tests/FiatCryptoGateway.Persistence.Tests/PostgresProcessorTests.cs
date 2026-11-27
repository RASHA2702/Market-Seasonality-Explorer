using FiatCryptoGateway.Application.Compliance;
using FiatCryptoGateway.Application.Payments;
using FiatCryptoGateway.Application.Ports;
using FiatCryptoGateway.Application.Reconciliation;
using FiatCryptoGateway.Domain;
using FiatCryptoGateway.Domain.Ledger;
using FiatCryptoGateway.Domain.Orders;
using FiatCryptoGateway.Domain.Payments;
using FiatCryptoGateway.Sandbox;
using Microsoft.Extensions.DependencyInjection;

namespace FiatCryptoGateway.Persistence.Tests;

/// <summary>The gateway's own processor on the real database: its records, its locks across instances, and its books.</summary>
[Collection(SharedPostgres.Name)]
public sealed class PostgresProcessorTests(PostgresFixture fixture)
{
    private const string Processor = "Processor";
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task ACardPaymentWithACode_IsStoredWithItsHistory_CapturedAndRecordedOnTheOrder()
    {
        await using var services = fixture.BuildServices(TimeProvider.System, Processor);
        var processor = services.GetRequiredService<PaymentProcessor>();
        var order = await PostgresOrders.CreateAsync(services);

        var waiting = await processor.StartAsync(order.OrderId, new CardInput("4242 4242 4242 4242", 12, DateTime.UtcNow.Year + 2, "737", null), None);
        Assert.Equal(PaymentAttemptStatus.AwaitingAuthentication, waiting.Status);
        var paid = await processor.AuthenticateAsync(order.OrderId, waiting.AttemptId, new OtpResponse(SandboxTestInstruments.Otp), None);

        Assert.True(paid.IsPaid);
        var stored = (await services.GetRequiredService<IPaymentAttemptStore>().FindAsync(paid.AttemptId, None))!;
        Assert.Equal(PaymentIdentifiers.Payment(paid.AttemptId), stored.ProviderPaymentId);
        Assert.NotNull(stored.RecordedOnOrderAt);
        Assert.Equal(
            [PaymentAttemptStatus.Started, PaymentAttemptStatus.AwaitingAuthentication, PaymentAttemptStatus.Authorized, PaymentAttemptStatus.Captured],
            stored.Events.Select(entry => entry.To));
        Assert.Equal(OrderStatus.CryptoSent, (await services.GetRequiredService<IOrderStore>().FindAsync(order.OrderId, None))!.Status);
        var entry = Assert.Single(await services.GetRequiredService<ILedgerStore>().ListAsync(order.OrderId, None), line => line.Kind == EntryKinds.FiatReceived);
        Assert.Equal((GatewayProcessorProvider.ProviderName, stored.ProviderPaymentId), (entry.External!.Provider, entry.External.Id));
    }

    [Fact]
    public async Task TwoGatewayInstancesPayingTheSameOrderAtOnce_ChargeItOnce()
    {
        await using var first = fixture.BuildServices(TimeProvider.System, Processor);
        await using var second = fixture.BuildServices(TimeProvider.System, Processor);

        for (var round = 0; round < 10; round++)
        {
            var order = await PostgresOrders.CreateAsync(first);

            var outcomes = await Task.WhenAll(Task.Run(() => TryPayAsync(first, order)), Task.Run(() => TryPayAsync(second, order)));

            // The second customer is turned away, or finds their attempt ended: the instances share locks and records.
            Assert.All(
                outcomes.OfType<GatewayRuleException>(),
                refusal => Assert.Contains(refusal.Code, new[] { "ORDER_NOT_PAYABLE", "ORDER_ALREADY_PAID", "PAYMENT_IN_PROGRESS" }));
            var attempts = await first.GetRequiredService<IPaymentAttemptStore>().ListForOrderAsync(order.OrderId, None);
            Assert.Single(attempts, attempt => attempt.WasCaptured);
            Assert.Equal(1, (await first.GetRequiredService<ILedgerStore>().ListAsync(order.OrderId, None)).Count(line => line.Kind == EntryKinds.FiatReceived));
        }
    }

    [Fact]
    public async Task TwoGatewayInstancesStartingAtOnce_LeaveOneAttemptWaiting_AndEndTheOther()
    {
        await using var first = fixture.BuildServices(TimeProvider.System, Processor);
        await using var second = fixture.BuildServices(TimeProvider.System, Processor);
        var year = DateTime.UtcNow.Year + 2;

        for (var round = 0; round < 10; round++)
        {
            var order = await PostgresOrders.CreateAsync(first);

            await Task.WhenAll(
                Task.Run(() => first.GetRequiredService<PaymentProcessor>().StartAsync(order.OrderId, new CardInput("4242 4242 4242 4242", 12, year, "737", null), None)),
                Task.Run(() => second.GetRequiredService<PaymentProcessor>().StartAsync(order.OrderId, new CardInput("4242 4242 4242 4242", 12, year, "737", null), None)));

            // One live attempt per order: only the payment lock shared through the database keeps two instances from both starting.
            var attempts = await first.GetRequiredService<IPaymentAttemptStore>().ListForOrderAsync(order.OrderId, None);
            Assert.Equal(2, attempts.Count);
            Assert.Single(attempts, attempt => attempt.Status == PaymentAttemptStatus.AwaitingAuthentication);
            Assert.Single(attempts, attempt => attempt is { Status: PaymentAttemptStatus.Expired, DeclineCode: DeclineCodes.Superseded });
        }
    }

    [Fact]
    public async Task RejectedMoney_IsRefundedThroughTheProcessor_AfterTheReview()
    {
        await using var services = fixture.BuildServices(TimeProvider.System, Processor);
        var order = await PostgresOrders.CreateAsync(services, 6_000m, "USD");
        var paid = await services.GetRequiredService<PaymentProcessor>().PayWithTestUpiAsync(order.OrderId, None);
        Assert.True(paid.IsPaid);

        await ReviewAndReleaseAsync(services, order);

        Assert.Equal(OrderStatus.Refunded, (await services.GetRequiredService<IOrderStore>().FindAsync(order.OrderId, None))!.Status);
        var stored = (await services.GetRequiredService<IPaymentAttemptStore>().FindAsync(paid.AttemptId, None))!;
        Assert.Equal((PaymentAttemptStatus.Refunded, PaymentIdentifiers.Refund(paid.AttemptId)), (stored.Status, stored.RefundId));
        var refund = Assert.Single(await services.GetRequiredService<ILedgerStore>().ListAsync(order.OrderId, None), line => line.Kind == EntryKinds.FiatRefunded);
        Assert.Equal((GatewayProcessorProvider.ProviderName, stored.RefundId), (refund.External!.Provider, refund.External.Id));
    }

    [Fact]
    public async Task ADayOfPaymentsAndARefund_ReconcilesCleanly_AgainstTheRealLedger()
    {
        // A day far from every other test's, so only this test's payments are in its report and its books.
        var time = new ManualTimeProvider(new DateTimeOffset(2033, 3, 10, 12, 0, 0, TimeSpan.Zero));
        await using var services = fixture.BuildServices(time, Processor);
        var processor = services.GetRequiredService<PaymentProcessor>();
        var paid = await PostgresOrders.CreateAsync(services);
        await processor.PayWithTestUpiAsync(paid.OrderId, None);
        var held = await PostgresOrders.CreateAsync(services, 6_000m, "USD");
        await processor.PayWithTestUpiAsync(held.OrderId, None);
        await ReviewAndReleaseAsync(services, held);

        time.Advance(TimeSpan.FromHours(13)); // 06:30 IST the next day
        var run = Assert.Single(await services.GetRequiredService<ReconciliationService>().RunAsync(new DateOnly(2033, 3, 10), None));

        // Two payments and one refund, in the processor's own records and in the books.
        Assert.Equal((GatewayProcessorProvider.ProviderName, 3, 3, 3), (run.Provider, run.ReportLines, run.LedgerEntries, run.Matched));
        Assert.Empty(run.Discrepancies);
    }

    private static async Task ReviewAndReleaseAsync(ServiceProvider services, PaymentOrder order)
    {
        var reviews = services.GetRequiredService<ComplianceReviewService>();
        await reviews.ReviewAsync(order.OrderId, ReviewActionKind.Reject, "analyst-1", "Source of funds not explained.", null, None);
        await reviews.ReviewAsync(order.OrderId, ReviewActionKind.FreezeAndReport, "analyst-1", "Reported as suspicious.", "STR-2026-0001", None);
        await reviews.ReviewAsync(order.OrderId, ReviewActionKind.ReleaseForRefund, "analyst-1", "Release instructed in writing.", null, None);
    }

    private static async Task<Exception?> TryPayAsync(ServiceProvider instance, PaymentOrder order)
    {
        try
        {
            await instance.GetRequiredService<PaymentProcessor>().PayWithTestUpiAsync(order.OrderId, None);
            return null;
        }
        catch (GatewayRuleException exception)
        {
            return exception;
        }
    }
}
