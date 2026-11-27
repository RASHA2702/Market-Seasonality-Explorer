using FiatCryptoGateway.Application.Payments;
using FiatCryptoGateway.Domain.Assets;
using FiatCryptoGateway.Domain.Payments;
using FiatCryptoGateway.Sandbox;

namespace FiatCryptoGateway.Tests;

public sealed class PaymentAttemptStoreTests
{
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly DateTimeOffset Start = Fixtures.Start;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    private readonly InMemoryPaymentAttemptStore _store = new();

    [Fact]
    public async Task ASavedAttempt_IsFoundAsACopy_NotTheSameObject()
    {
        var attempt = New(Guid.NewGuid(), Start);
        await _store.AddAsync(attempt, None);

        var found = await _store.FindAsync(attempt.AttemptId, None);
        found!.Authorize("rail_1", Start.AddSeconds(1)); // not saved

        Assert.Equal(PaymentAttemptStatus.Started, (await _store.FindAsync(attempt.AttemptId, None))!.Status);
        Assert.Null(await _store.FindAsync(Guid.NewGuid(), None));
    }

    [Fact]
    public async Task EachSave_RaisesTheVersion_AndAStaleCopyCannotOverwriteANewerOne()
    {
        var attempt = New(Guid.NewGuid(), Start);
        await _store.AddAsync(attempt, None);
        var first = (await _store.FindAsync(attempt.AttemptId, None))!;
        var second = (await _store.FindAsync(attempt.AttemptId, None))!;

        first.Authorize("rail_1", Start.AddSeconds(1));
        await _store.SaveAsync(first, None);
        second.Decline(DeclineCodes.DoNotHonor, Start.AddSeconds(2));

        Assert.Equal(2, first.Version);
        await Assert.ThrowsAsync<PaymentAttemptConcurrencyException>(() => _store.SaveAsync(second, None));
        Assert.Equal(PaymentAttemptStatus.Authorized, (await _store.FindAsync(attempt.AttemptId, None))!.Status);
    }

    [Fact]
    public async Task AnOrder_CanHaveSeveralAttempts_ButOnlyOneCaptured()
    {
        var orderId = Guid.NewGuid();
        var declined = New(orderId, Start);
        var paid = New(orderId, Start.AddMinutes(1));
        var secondPayment = New(orderId, Start.AddMinutes(2));
        await _store.AddAsync(declined, None);
        await _store.AddAsync(paid, None);
        await _store.AddAsync(secondPayment, None);
        declined.Decline(DeclineCodes.DoNotHonor, Start.AddSeconds(30));
        await _store.SaveAsync(declined, None);
        paid.Authorize("rail_1", Start.AddMinutes(1));
        paid.Capture("gp_pay_1", Start.AddMinutes(1));
        await _store.SaveAsync(paid, None);
        secondPayment.Authorize("rail_2", Start.AddMinutes(2));
        secondPayment.Capture("gp_pay_2", Start.AddMinutes(2));

        await Assert.ThrowsAsync<PaymentAttemptConcurrencyException>(() => _store.SaveAsync(secondPayment, None));

        Assert.Equal([declined.AttemptId, paid.AttemptId, secondPayment.AttemptId], (await _store.ListForOrderAsync(orderId, None)).Select(attempt => attempt.AttemptId));
        Assert.Equal(paid.AttemptId, (await _store.FindCapturedForOrderAsync(orderId, None))!.AttemptId);
        Assert.Null(await _store.FindCapturedForOrderAsync(Guid.NewGuid(), None));
    }

    [Fact]
    public async Task ARefundedAttempt_IsStillTheOrdersCapturedPayment()
    {
        var attempt = New(Guid.NewGuid(), Start);
        await _store.AddAsync(attempt, None);
        attempt.Authorize("rail_1", Start.AddSeconds(1));
        attempt.Capture("gp_pay_1", Start.AddSeconds(1));
        attempt.Refund("gp_rfd_1", Start.AddHours(1));
        await _store.SaveAsync(attempt, None);

        Assert.Equal(PaymentAttemptStatus.Refunded, (await _store.FindCapturedForOrderAsync(attempt.OrderId, None))!.Status);
    }

    [Fact]
    public async Task AnAttempt_IsAddedOnce()
    {
        var attempt = New(Guid.NewGuid(), Start);
        await _store.AddAsync(attempt, None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.AddAsync(attempt, None));
    }

    [Fact]
    public async Task APaymentIdentifier_BelongsToOneAttemptOnly_AndReuseIsABugNotALostRace()
    {
        var first = await Captured(Start);
        var other = New(Guid.NewGuid(), Start);
        await _store.AddAsync(other, None);
        other.Authorize("rail", Start);
        other.Capture(first.ProviderPaymentId!, Start);

        // Not a PaymentAttemptConcurrencyException: callers handle that one as a lost race, and this is not one.
        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.SaveAsync(other, None));

        Assert.Equal(PaymentAttemptStatus.Started, (await _store.FindAsync(other.AttemptId, None))!.Status);
    }

    [Fact]
    public async Task ARefundIdentifier_BelongsToOneAttemptOnly_AndReuseIsABugNotALostRace()
    {
        var refunded = await Captured(Start.AddHours(-30), refundedAt: Start.AddHours(1));
        var other = await Captured(Start);
        other.MarkRecordedOnOrder(Start);
        other.Refund(refunded.RefundId!, Start.AddHours(2));

        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.SaveAsync(other, None));

        Assert.Equal(PaymentAttemptStatus.Captured, (await _store.FindAsync(other.AttemptId, None))!.Status);
    }

    [Fact]
    public async Task CapturedPayments_NotYetOnTheirOrder_AreListedOldestFirst_UntilRecorded()
    {
        var older = await Captured(Start);
        var newer = await Captured(Start.AddMinutes(5));
        var recorded = await Captured(Start.AddMinutes(1));
        recorded.MarkRecordedOnOrder(Start.AddMinutes(2));
        await _store.SaveAsync(recorded, None);
        await _store.AddAsync(New(Guid.NewGuid(), Start), None); // not captured

        var pending = await _store.ListCapturedNotRecordedAsync(10, None);

        Assert.Equal([older.AttemptId, newer.AttemptId], pending.Select(attempt => attempt.AttemptId));
        Assert.Single(await _store.ListCapturedNotRecordedAsync(1, None));
    }

    [Fact]
    public async Task OnlyApprovedPayments_NotYetCaptured_AreListedOldestFirst_ForTheSweep()
    {
        var older = await Authorized(Start);
        var newer = await Authorized(Start.AddMinutes(5));
        await Authorized(Start.AddMinutes(30)); // too recent to be stuck
        await Captured(Start);                  // finished
        await _store.AddAsync(New(Guid.NewGuid(), Start), None); // only started, never approved

        var stuck = await _store.ListStuckAuthorizedAsync(Start.AddMinutes(10), 10, None);

        Assert.Equal([older.AttemptId, newer.AttemptId], stuck.Select(attempt => attempt.AttemptId));
        Assert.Single(await _store.ListStuckAuthorizedAsync(Start.AddMinutes(10), 1, None));

        // Last changed exactly at the cutoff counts as stuck, and a moment later does not.
        Assert.Equal([older.AttemptId], (await _store.ListStuckAuthorizedAsync(Start, 10, None)).Select(attempt => attempt.AttemptId));
        Assert.Empty(await _store.ListStuckAuthorizedAsync(Start.AddTicks(-1), 10, None));
    }

    [Fact]
    public async Task OnlyPendingAttempts_PastTheirTimeLimit_AreListedForExpiry()
    {
        var late = New(Guid.NewGuid(), Start);
        var later = New(Guid.NewGuid(), Start.AddMinutes(1));
        var notYet = New(Guid.NewGuid(), Start.AddMinutes(20));
        var captured = await Captured(Start);
        await _store.AddAsync(late, None);
        await _store.AddAsync(later, None);
        await _store.AddAsync(notYet, None);

        var due = await _store.ListPendingExpiredAsync(Start.AddMinutes(12), 10, None);

        Assert.Equal([late.AttemptId, later.AttemptId], due.Select(attempt => attempt.AttemptId));
        Assert.DoesNotContain(captured.AttemptId, due.Select(attempt => attempt.AttemptId));
        Assert.Single(await _store.ListPendingExpiredAsync(Start.AddMinutes(12), 1, None));

        // Due exactly at its time limit, and not a moment before.
        Assert.Equal([late.AttemptId], (await _store.ListPendingExpiredAsync(late.ExpiresAt, 10, None)).Select(attempt => attempt.AttemptId));
        Assert.Empty(await _store.ListPendingExpiredAsync(late.ExpiresAt.AddTicks(-1), 10, None));
    }

    [Fact]
    public async Task TheDailyReport_ListsWhatWasCapturedOrRefundedInTheWindow()
    {
        var inWindow = await Captured(Start.AddHours(1));
        var before = await Captured(Start.AddHours(-30));
        var refundedInWindow = await Captured(Start.AddHours(-30), refundedAt: Start.AddHours(2));
        var refundedAfter = await Captured(Start.AddHours(-30), refundedAt: Start.AddHours(30));
        var atTheStart = await Captured(Start);
        var atTheEnd = await Captured(Start.AddDays(1));

        var settled = await _store.ListSettledBetweenAsync(Start, Start.AddDays(1), None);

        // A window includes its start and excludes its end, so a payment is in exactly one day's report.
        Assert.Equal(
            new[] { inWindow.AttemptId, refundedInWindow.AttemptId, atTheStart.AttemptId }.Order(),
            settled.Select(attempt => attempt.AttemptId).Order());
        Assert.DoesNotContain(before.AttemptId, settled.Select(attempt => attempt.AttemptId));
        Assert.DoesNotContain(refundedAfter.AttemptId, settled.Select(attempt => attempt.AttemptId));
        Assert.DoesNotContain(atTheEnd.AttemptId, settled.Select(attempt => attempt.AttemptId));
    }

    private static PaymentAttempt New(Guid orderId, DateTimeOffset at) =>
        PaymentAttempt.Start(
            Guid.NewGuid(), orderId, Fixtures.ClientId, new Money(10_000m, Fixtures.Inr),
            new PaymentInstrument(PaymentMethod.Upi, "success@sandbox"), at, Window);

    private async Task<PaymentAttempt> Authorized(DateTimeOffset at)
    {
        var attempt = New(Guid.NewGuid(), at);
        await _store.AddAsync(attempt, None);
        attempt.Authorize("rail", at);
        await _store.SaveAsync(attempt, None);
        return attempt;
    }

    private async Task<PaymentAttempt> Captured(DateTimeOffset capturedAt, DateTimeOffset? refundedAt = null)
    {
        var attempt = New(Guid.NewGuid(), capturedAt);
        await _store.AddAsync(attempt, None);
        attempt.Authorize("rail", capturedAt);
        attempt.Capture($"gp_pay_{attempt.AttemptId:N}", capturedAt);
        if (refundedAt is { } refunded)
        {
            attempt.MarkRecordedOnOrder(capturedAt);
            attempt.Refund($"gp_rfd_{attempt.AttemptId:N}", refunded);
        }

        await _store.SaveAsync(attempt, None);
        return attempt;
    }
}
