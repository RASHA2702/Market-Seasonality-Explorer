using System.Text.Json;
using System.Text.RegularExpressions;
using FiatCryptoGateway.Application;
using FiatCryptoGateway.Application.OnRamp;
using FiatCryptoGateway.Application.Payments;
using FiatCryptoGateway.Domain;
using FiatCryptoGateway.Domain.Ledger;
using FiatCryptoGateway.Domain.Orders;
using FiatCryptoGateway.Domain.Payments;
using FiatCryptoGateway.Sandbox;
using Microsoft.Extensions.DependencyInjection;

namespace FiatCryptoGateway.Tests;

/// <summary>The gateway's own processor through the real services and the sandbox card and UPI networks.</summary>
public sealed class PaymentProcessorTests : IDisposable
{
    private static readonly CancellationToken None = CancellationToken.None;

    private readonly ProcessorHarness _h = new();

    public void Dispose() => _h.Dispose();

    // ---- paying ----

    [Fact]
    public async Task UpiPayment_NeedsTheCustomersApproval_ThenIsCapturedAndRecordedOnTheOrder()
    {
        var order = await _h.CreateOrderAsync();

        var started = await _h.StartUpiAsync(order);

        Assert.Equal((PaymentAttemptStatus.AwaitingAuthentication, ChallengeKind.UpiApproval), (started.Status, started.Challenge));
        Assert.Equal(OrderStatus.AwaitingFiatPayment, await StatusAsync(order)); // nothing is taken before the customer approves
        Assert.Equal(0, await _h.LedgerEntriesAsync(order, EntryKinds.FiatReceived));

        var paid = await _h.ApproveAsync(order, started);

        Assert.True(paid.IsPaid);
        var stored = await _h.StoredAsync(paid);
        Assert.Equal(PaymentIdentifiers.Payment(paid.AttemptId), stored.ProviderPaymentId);
        Assert.NotNull(stored.RecordedOnOrderAt);
        var current = (await _h.Orders.FindAsync(order.OrderId, None))!;
        Assert.Equal(OrderStatus.CryptoSent, current.Status);
        Assert.Equal((GatewayProcessorProvider.ProviderName, stored.ProviderPaymentId), (current.FiatPayment!.Provider, current.FiatPayment.ProviderPaymentId));
        Assert.Equal(1, await _h.LedgerEntriesAsync(order, EntryKinds.FiatReceived));
    }

    [Fact]
    public async Task CardWithoutAuthentication_IsCapturedAtOnce()
    {
        var order = await _h.CreateOrderAsync();

        var paid = await _h.StartCardAsync(order, "4111 1111 1111 1111");

        Assert.True(paid.IsPaid);
        Assert.Equal("Visa ····1111", paid.InstrumentLabel);
        Assert.Equal(OrderStatus.CryptoSent, await StatusAsync(order));
    }

    [Theory]
    [InlineData("4242 4242 4242 4242", "Visa ····4242")]
    [InlineData("5555 5555 5555 4444", "Mastercard ····4444")]
    public async Task CardWithAOneTimePassword_WaitsForTheCode_ThenIsCaptured(string number, string label)
    {
        var order = await _h.CreateOrderAsync();

        var waiting = await _h.StartCardAsync(order, number);

        Assert.Equal(
            (PaymentAttemptStatus.AwaitingAuthentication, ChallengeKind.Otp, 3, label),
            (waiting.Status, waiting.Challenge, waiting.TriesLeft, waiting.InstrumentLabel));
        Assert.Equal(OrderStatus.AwaitingFiatPayment, await StatusAsync(order));

        var paid = await _h.EnterCodeAsync(order, waiting);

        Assert.True(paid.IsPaid);
        Assert.Equal(OrderStatus.CryptoSent, await StatusAsync(order));
    }

    [Fact]
    public async Task WrongCodes_UseUpTheTries_ThenTheAttemptFails_AndTheOrderCanStillBePaid()
    {
        var order = await _h.CreateOrderAsync();
        var waiting = await _h.StartCardAsync(order, "4242 4242 4242 4242");

        var first = await _h.EnterCodeAsync(order, waiting, "000000");
        var second = await _h.EnterCodeAsync(order, waiting, "000000");
        var third = await _h.EnterCodeAsync(order, waiting, "000000");

        Assert.Equal((PaymentAttemptStatus.AwaitingAuthentication, 2), (first.Status, first.TriesLeft));
        Assert.Equal((PaymentAttemptStatus.AwaitingAuthentication, 1), (second.Status, second.TriesLeft));
        Assert.Equal((PaymentAttemptStatus.AuthenticationFailed, DeclineCodes.AuthenticationFailed), (third.Status, third.DeclineCode));
        Assert.Equal(OrderStatus.AwaitingFiatPayment, await StatusAsync(order));
        Assert.Equal(0, await _h.LedgerEntriesAsync(order, EntryKinds.FiatReceived));

        var again = await _h.StartCardAsync(order, "4242 4242 4242 4242");
        Assert.True((await _h.EnterCodeAsync(order, again)).IsPaid);
    }

    [Fact]
    public async Task TheCardWhoseCodeIsNeverAccepted_FailsEvenWithTheRightCode()
    {
        var order = await _h.CreateOrderAsync();
        var waiting = await _h.StartCardAsync(order, "4000 0000 0000 3220");

        await _h.EnterCodeAsync(order, waiting);
        await _h.EnterCodeAsync(order, waiting);
        var last = await _h.EnterCodeAsync(order, waiting);

        Assert.Equal(PaymentAttemptStatus.AuthenticationFailed, last.Status);
        Assert.Equal(0, _h.CardRail.CaptureCalls);
    }

    [Theory]
    [InlineData("4000 0000 0000 0002", "DO_NOT_HONOR")]
    [InlineData("4000 0000 0000 9995", "INSUFFICIENT_FUNDS")]
    [InlineData("4000 0000 0000 0069", "EXPIRED_CARD")]
    [InlineData("4000 0000 0000 0127", "INCORRECT_SECURITY_CODE")]
    public async Task ABankDecline_EndsTheAttempt_AndTouchesNothingElse(string number, string code)
    {
        var order = await _h.CreateOrderAsync();

        var declined = await _h.StartCardAsync(order, number);

        Assert.Equal((PaymentAttemptStatus.Declined, code), (declined.Status, declined.DeclineCode));
        Assert.False(declined.IsPending);
        Assert.Equal(OrderStatus.AwaitingFiatPayment, await StatusAsync(order));
        Assert.Equal(0, await _h.LedgerEntriesAsync(order, EntryKinds.FiatReceived));
        Assert.Equal(0, _h.CardRail.CaptureCalls);
    }

    [Theory]
    [InlineData("card")]
    [InlineData("upi")]
    public async Task AnUnavailableNetwork_FailsTheAttempt_WithoutTakingMoney(string method)
    {
        var order = await _h.CreateOrderAsync();

        var failed = method == "card"
            ? await _h.StartCardAsync(order, "4000 0000 0000 0259")
            : await _h.StartUpiAsync(order, "unavailable@sandbox");

        Assert.Equal((PaymentAttemptStatus.Failed, DeclineCodes.RailUnavailable), (failed.Status, failed.DeclineCode));
        Assert.Equal(0, _h.CardRail.CaptureCalls + _h.UpiRail.CaptureCalls);
    }

    [Fact]
    public async Task UpiScenarios_EndAsTheCustomersBankAnswers_AndTheOrderIsStillPayable()
    {
        var order = await _h.CreateOrderAsync();

        var invalid = await _h.StartUpiAsync(order, "invalid@sandbox");
        var insufficient = await _h.ApproveAsync(order, await _h.StartUpiAsync(order, "insufficient@sandbox"));
        var refused = await _h.AnswerAsync(order, await _h.StartUpiAsync(order), new ApprovalResponse(approved: false));
        var timeout = await _h.ApproveAsync(order, await _h.StartUpiAsync(order, "timeout@sandbox"));

        Assert.Equal((PaymentAttemptStatus.Declined, DeclineCodes.InvalidUpiAddress), (invalid.Status, invalid.DeclineCode));
        Assert.Equal((PaymentAttemptStatus.Declined, DeclineCodes.InsufficientFunds), (insufficient.Status, insufficient.DeclineCode));
        Assert.Equal((PaymentAttemptStatus.Declined, DeclineCodes.CustomerDeclined), (refused.Status, refused.DeclineCode));
        Assert.Equal((PaymentAttemptStatus.Declined, DeclineCodes.TimedOut), (timeout.Status, timeout.DeclineCode));
        Assert.Equal(OrderStatus.AwaitingFiatPayment, await StatusAsync(order));

        Assert.True((await _h.ApproveAsync(order, await _h.StartUpiAsync(order))).IsPaid);
    }

    [Theory]
    [InlineData("4012 8888 8888 1881", "TEST_CARDS_ONLY")] // looks real and passes the checksum
    [InlineData("4111 1111 1111 1112", "INVALID_CARD_NUMBER")] // fails the checksum
    public async Task ARealOrMistypedCardNumber_IsRefused_AndNothingIsKept(string number, string code)
    {
        var order = await _h.CreateOrderAsync();

        var refusal = await Assert.ThrowsAsync<GatewayRuleException>(() => _h.StartCardAsync(order, number));

        Assert.Equal(code, refusal.Code);
        var digits = number.Replace(" ", string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(digits, refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(digits[^4..], refusal.Message, StringComparison.Ordinal);
        Assert.Empty(await _h.Attempts.ListForOrderAsync(order.OrderId, None));
    }

    // ---- what an order allows ----

    [Fact]
    public async Task APaidOrder_CannotBePaidAgain()
    {
        var order = await _h.CreateOrderAsync();
        await _h.StartCardAsync(order, "4111 1111 1111 1111");

        var refusal = await Assert.ThrowsAsync<GatewayRuleException>(() => _h.StartCardAsync(order, "4111 1111 1111 1111"));

        Assert.Equal("ORDER_NOT_PAYABLE", refusal.Code);
        Assert.Equal(1, await _h.LedgerEntriesAsync(order, EntryKinds.FiatReceived));
    }

    [Fact]
    public async Task ACapturedPayment_NotYetRecordedOnItsOrder_StillBlocksAnotherPayment()
    {
        var order = await _h.CreateOrderAsync();
        await _h.SeedCapturedAsync(order);

        var refusal = await Assert.ThrowsAsync<GatewayRuleException>(() => _h.StartUpiAsync(order));

        Assert.Equal("ORDER_ALREADY_PAID", refusal.Code);
    }

    [Fact]
    public async Task AnOrderThatCannotBePaid_IsReportedBeforeTheCardIsChecked()
    {
        var order = await _h.CreateOrderAsync();
        _h.Time.Advance(TimeSpan.FromMinutes(16));

        var refusal = await Assert.ThrowsAsync<GatewayRuleException>(() => _h.StartCardAsync(order, "4012 8888 8888 1881"));

        Assert.Equal("ORDER_NOT_PAYABLE", refusal.Code);
    }

    [Fact]
    public async Task AnOrderPastItsDeadline_CannotBePaid()
    {
        var order = await _h.CreateOrderAsync();
        _h.Time.Advance(TimeSpan.FromMinutes(16));

        var refusal = await Assert.ThrowsAsync<GatewayRuleException>(() => _h.StartUpiAsync(order));

        Assert.Equal("ORDER_NOT_PAYABLE", refusal.Code);
        Assert.Empty(await _h.Attempts.ListForOrderAsync(order.OrderId, None));
    }

    [Fact]
    public async Task AnUnknownOrder_IsNotFound()
    {
        var missing = await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            _h.Processor.StartAsync(Guid.NewGuid(), new UpiInput(SandboxTestInstruments.SuccessfulUpiAddress), None));

        Assert.Equal("ORDER_NOT_FOUND", missing.Code);
    }

    [Fact]
    public async Task AnAttempt_NeverOutlivesItsOrdersDeadline()
    {
        var order = await _h.CreateOrderAsync();
        var early = await _h.StartCardAsync(order, "4242 4242 4242 4242");
        Assert.Equal(_h.Time.GetUtcNow().AddMinutes(10), early.ExpiresAt);

        _h.Time.Advance(TimeSpan.FromMinutes(12)); // three minutes of the order's fifteen are left
        var late = await _h.StartCardAsync(order, "4242 4242 4242 4242");

        Assert.Equal(order.PaymentDeadline, late.ExpiresAt);
        Assert.True(late.ExpiresAt < _h.Time.GetUtcNow().AddMinutes(10));
    }

    [Fact]
    public async Task StartingAgain_EndsTheAttemptStillWaiting()
    {
        var order = await _h.CreateOrderAsync();
        var first = await _h.StartCardAsync(order, "4242 4242 4242 4242");
        var second = await _h.StartCardAsync(order, "4242 4242 4242 4242");

        var ended = await _h.StoredAsync(first);
        Assert.Equal((PaymentAttemptStatus.Expired, DeclineCodes.Superseded), (ended.Status, ended.DeclineCode));

        // The old attempt's code is not taken any more: answering it just reports where it stands.
        var stale = await _h.EnterCodeAsync(order, first);
        Assert.Equal(PaymentAttemptStatus.Expired, stale.Status);
        Assert.Equal(OrderStatus.AwaitingFiatPayment, await StatusAsync(order));

        Assert.True((await _h.EnterCodeAsync(order, second)).IsPaid);
    }

    [Fact]
    public async Task TooManyAttempts_AreRefused()
    {
        using var h = new ProcessorHarness(new Dictionary<string, string?> { ["Gateway:Processor:MaxAttemptsPerOrder"] = "2" });
        var order = await h.CreateOrderAsync();
        await h.StartCardAsync(order, "4000 0000 0000 0002");
        await h.StartCardAsync(order, "4000 0000 0000 0002");

        var refusal = await Assert.ThrowsAsync<GatewayRuleException>(() => h.StartCardAsync(order, "4111 1111 1111 1111"));

        Assert.Equal("TOO_MANY_ATTEMPTS", refusal.Code);
        Assert.Equal(2, (await h.Attempts.ListForOrderAsync(order.OrderId, None)).Count);
    }

    [Fact]
    public async Task AnApprovedPaymentBeingCompleted_BlocksAnotherAttempt()
    {
        var order = await _h.CreateOrderAsync();
        _h.UpiRail.FailCaptures = 1;
        var approved = await _h.ApproveAsync(order, await _h.StartUpiAsync(order));
        Assert.Equal(PaymentAttemptStatus.Authorized, approved.Status);

        var refusal = await Assert.ThrowsAsync<GatewayRuleException>(() => _h.StartUpiAsync(order));

        Assert.Equal("PAYMENT_IN_PROGRESS", refusal.Code);
    }

    [Fact]
    public async Task AnAttempt_CanOnlyBeAnsweredOnItsOwnOrder()
    {
        var order = await _h.CreateOrderAsync();
        var other = await _h.CreateOrderAsync();
        var waiting = await _h.StartCardAsync(order, "4242 4242 4242 4242");

        var wrongOrder = await Assert.ThrowsAsync<ResourceNotFoundException>(() => _h.EnterCodeAsync(other, waiting));
        var unknown = await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            _h.Processor.AuthenticateAsync(order.OrderId, Guid.NewGuid(), new OtpResponse(SandboxTestInstruments.Otp), None));

        Assert.Equal(("ATTEMPT_NOT_FOUND", "ATTEMPT_NOT_FOUND"), (wrongOrder.Code, unknown.Code));
        Assert.Null(await _h.Processor.FindAttemptAsync(other.OrderId, waiting.AttemptId, None));
        Assert.NotNull(await _h.Processor.FindAttemptAsync(order.OrderId, waiting.AttemptId, None));
        Assert.Equal(waiting.AttemptId, (await _h.Processor.LatestAttemptAsync(order.OrderId, None))!.AttemptId);
        Assert.Null(await _h.Processor.LatestAttemptAsync(other.OrderId, None));
    }

    [Fact]
    public async Task AnsweringTwice_ChargesOnce()
    {
        var order = await _h.CreateOrderAsync();
        var waiting = await _h.StartCardAsync(order, "4242 4242 4242 4242");

        var paid = await _h.EnterCodeAsync(order, waiting);
        var again = await _h.EnterCodeAsync(order, waiting);

        Assert.True(paid.IsPaid);
        Assert.True(again.IsPaid);
        Assert.Equal(1, _h.CardRail.CaptureCalls);
        Assert.Equal(1, await _h.LedgerEntriesAsync(order, EntryKinds.FiatReceived));
    }

    [Fact]
    public async Task AnOrderClosedWhileTheCustomerTypesTheCode_EndsTheAttempt_WithoutAskingTheNetwork()
    {
        var order = await _h.CreateOrderAsync();
        var waiting = await _h.StartCardAsync(order, "4242 4242 4242 4242");

        // The order is closed from outside while the attempt's own time has not run out.
        Assert.True((await _h.Orders.FindAsync(order.OrderId, None))!.ExpireIfOverdue(order.PaymentDeadline.AddSeconds(1)));
        var ended = await _h.EnterCodeAsync(order, waiting);

        Assert.Equal((PaymentAttemptStatus.Expired, DeclineCodes.OrderClosed), (ended.Status, ended.DeclineCode));
        Assert.Equal(0, _h.CardRail.CaptureCalls);
    }

    // ---- crashes, outages and the background sweep ----

    [Fact]
    public async Task ACaptureThatFails_LeavesTheApprovedPayment_AndTheSweepFinishesIt()
    {
        var order = await _h.CreateOrderAsync();
        _h.UpiRail.FailCaptures = 1;

        var approved = await _h.ApproveAsync(order, await _h.StartUpiAsync(order));

        // The customer is told their payment is being completed, not shown an error.
        Assert.Equal(PaymentAttemptStatus.Authorized, approved.Status);
        Assert.True(approved.IsPending);
        Assert.Equal(OrderStatus.AwaitingFiatPayment, await StatusAsync(order));
        Assert.Contains(_h.Logs.Lines, line => line.StartsWith("Warning", StringComparison.Ordinal) && line.Contains(approved.AttemptId.ToString(), StringComparison.Ordinal));

        Assert.Equal(new SweepResult(0, 0, 0, 0, 0), await _h.Processor.SweepAsync(None)); // not stuck yet
        _h.Time.Advance(TimeSpan.FromSeconds(119));
        Assert.Equal(new SweepResult(0, 0, 0, 0, 0), await _h.Processor.SweepAsync(None)); // a second short of two minutes
        _h.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(new SweepResult(0, 1, 0, 0, 0), await _h.Processor.SweepAsync(None)); // two minutes exactly

        var stored = await _h.StoredAsync(approved);
        Assert.Equal(PaymentAttemptStatus.Captured, stored.Status);
        Assert.NotNull(stored.RecordedOnOrderAt);
        Assert.Equal(OrderStatus.CryptoSent, await StatusAsync(order));
        Assert.Equal(2, _h.UpiRail.CaptureCalls);
        Assert.Equal(new SweepResult(0, 0, 0, 0, 0), await _h.Processor.SweepAsync(None)); // nothing is left to do
    }

    [Fact]
    public async Task AnApprovalThatCanNeverBeCaptured_IsRetriedThenGivenUpAfterTheLapse()
    {
        var order = await _h.CreateOrderAsync();
        _h.UpiRail.FailCaptures = int.MaxValue;
        var approved = await _h.ApproveAsync(order, await _h.StartUpiAsync(order));
        _h.Time.Advance(TimeSpan.FromMinutes(3));

        // While the approval is young, the failure is counted and the attempt stays for the next sweep.
        Assert.Equal(new SweepResult(0, 0, 0, 0, 1), await _h.Processor.SweepAsync(None));
        Assert.Equal(PaymentAttemptStatus.Authorized, (await _h.StoredAsync(approved)).Status);

        _h.Time.Advance(TimeSpan.FromHours(25));
        Assert.Equal(new SweepResult(0, 0, 0, 1, 0), await _h.Processor.SweepAsync(None));

        var given = await _h.StoredAsync(approved);
        Assert.Equal((PaymentAttemptStatus.Failed, DeclineCodes.AuthorizationLapsed), (given.Status, given.DeclineCode));
        Assert.Equal(0, await _h.LedgerEntriesAsync(order, EntryKinds.FiatReceived));
        Assert.Contains(_h.Logs.Lines, line => line.StartsWith("Error", StringComparison.Ordinal) && line.Contains("given up", StringComparison.Ordinal));
        Assert.Equal(new SweepResult(0, 0, 0, 0, 0), await _h.Processor.SweepAsync(None));
    }

    [Fact]
    public async Task TheLapse_IsExactlyTheConfiguredHours()
    {
        using var h = new ProcessorHarness(new Dictionary<string, string?> { ["Gateway:Processor:AuthorizationLapseHours"] = "1" });
        var order = await h.CreateOrderAsync();
        h.UpiRail.FailCaptures = int.MaxValue;
        var approved = await h.ApproveAsync(order, await h.StartUpiAsync(order));

        h.Time.Advance(TimeSpan.FromMinutes(59) + TimeSpan.FromSeconds(59));
        Assert.Equal(new SweepResult(0, 0, 0, 0, 1), await h.Processor.SweepAsync(None)); // a second short: still retried
        Assert.Equal(PaymentAttemptStatus.Authorized, (await h.StoredAsync(approved)).Status);

        h.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(new SweepResult(0, 0, 0, 1, 0), await h.Processor.SweepAsync(None)); // an hour exactly after the approval
        Assert.Equal(PaymentAttemptStatus.Failed, (await h.StoredAsync(approved)).Status);
    }

    [Fact]
    public async Task ACaptureThatCannotBeSaved_IsNotRecordedOnTheOrder_UntilTheSweepSavesIt()
    {
        var order = await _h.CreateOrderAsync();
        _h.AttemptStore.FailNextSaveWhen = attempt => attempt.Status == PaymentAttemptStatus.Captured;

        var view = await _h.StartCardAsync(order, "4111 1111 1111 1111");

        // The network took the money but the capture was not stored, so the order is not told and the customer is not
        // shown a payment the records do not hold.
        Assert.Equal(PaymentAttemptStatus.Authorized, view.Status);
        Assert.Equal(PaymentAttemptStatus.Authorized, (await _h.StoredAsync(view)).Status);
        Assert.Equal(OrderStatus.AwaitingFiatPayment, await StatusAsync(order));
        Assert.Equal(0, await _h.LedgerEntriesAsync(order, EntryKinds.FiatReceived));

        _h.Time.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal(new SweepResult(0, 1, 0, 0, 0), await _h.Processor.SweepAsync(None));

        Assert.Equal(PaymentAttemptStatus.Captured, (await _h.StoredAsync(view)).Status);
        Assert.Equal(OrderStatus.CryptoSent, await StatusAsync(order));
        Assert.Equal(2, _h.CardRail.CaptureCalls); // asked again, and the network answered the same capture
        Assert.Equal(1, await _h.LedgerEntriesAsync(order, EntryKinds.FiatReceived));
    }

    [Fact]
    public async Task ARecordingThatFails_ShowsTheCustomerNoError_AndTheOrderMovesOnWithoutAnotherPosting()
    {
        var order = await _h.CreateOrderAsync();
        _h.Liquidity.FailBuys = 1; // the exchange is down when the order moves on after the payment is recorded

        var paid = await _h.StartCardAsync(order, "4111 1111 1111 1111");

        Assert.True(paid.IsPaid); // the money is taken and kept, so the customer sees it
        Assert.Null((await _h.StoredAsync(paid)).RecordedOnOrderAt);
        Assert.Equal(OrderStatus.ReadyToSettle, await StatusAsync(order));
        Assert.Contains(_h.Logs.Lines, line => line.StartsWith("Warning", StringComparison.Ordinal) && line.Contains("Recording the captured payment", StringComparison.Ordinal));

        // The sweep finds the order already holds the payment, so it only marks it. The order's own worker moves it on.
        Assert.Equal(new SweepResult(0, 0, 1, 0, 0), await _h.Processor.SweepAsync(None));
        Assert.NotNull((await _h.StoredAsync(paid)).RecordedOnOrderAt);
        await _h.OnRamp.ProcessPendingOrdersAsync(None);

        Assert.Equal(OrderStatus.CryptoSent, await StatusAsync(order));
        Assert.Equal(1, await _h.LedgerEntriesAsync(order, EntryKinds.FiatReceived));
        Assert.DoesNotContain(_h.Logs.Lines, line => line.StartsWith("Error", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ACapturedPaymentNotOnItsOrder_IsRecordedBySweep()
    {
        var order = await _h.CreateOrderAsync();
        var seeded = await _h.SeedCapturedAsync(order);

        Assert.Equal(new SweepResult(0, 0, 1, 0, 0), await _h.Processor.SweepAsync(None));

        Assert.Equal(OrderStatus.CryptoSent, await StatusAsync(order));
        Assert.NotNull((await _h.Attempts.FindAsync(seeded.AttemptId, None))!.RecordedOnOrderAt);
        Assert.Equal(1, await _h.LedgerEntriesAsync(order, EntryKinds.FiatReceived));
        Assert.Equal(new SweepResult(0, 0, 0, 0, 0), await _h.Processor.SweepAsync(None));
    }

    [Fact]
    public async Task ACrashAfterTheOrderTookThePayment_IsNotRecordedTwice()
    {
        var order = await _h.CreateOrderAsync();
        var seeded = await _h.SeedCapturedAsync(order);
        await _h.OnRamp.RecordFiatPaymentAsync(
            new FiatPaymentConfirmation(GatewayProcessorProvider.ProviderName, seeded.ProviderPaymentId!, order.OrderId, seeded.Amount, seeded.CapturedAt!.Value), None);

        Assert.Equal(new SweepResult(0, 0, 1, 0, 0), await _h.Processor.SweepAsync(None));

        Assert.Equal(1, await _h.LedgerEntriesAsync(order, EntryKinds.FiatReceived));
        Assert.NotNull((await _h.Attempts.FindAsync(seeded.AttemptId, None))!.RecordedOnOrderAt);
        Assert.DoesNotContain(_h.Logs.Lines, line => line.StartsWith("Error", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PendingAttempts_PastTheirTimeLimit_AreEndedBySweep_AndOthersAreLeftAlone()
    {
        var order = await _h.CreateOrderAsync();
        var other = await _h.CreateOrderAsync();
        var waiting = await _h.StartCardAsync(order, "4242 4242 4242 4242");
        _h.Time.Advance(TimeSpan.FromMinutes(11));
        var fresh = await _h.StartCardAsync(other, "4242 4242 4242 4242"); // four minutes of its order's fifteen are left

        Assert.Equal(new SweepResult(1, 0, 0, 0, 0), await _h.Processor.SweepAsync(None));

        var ended = await _h.StoredAsync(waiting);
        Assert.Equal((PaymentAttemptStatus.Expired, DeclineCodes.TimedOut), (ended.Status, ended.DeclineCode));
        Assert.Equal(PaymentAttemptStatus.AwaitingAuthentication, (await _h.StoredAsync(fresh)).Status);
        Assert.Equal(new SweepResult(0, 0, 0, 0, 0), await _h.Processor.SweepAsync(None));
    }

    [Fact]
    public async Task AnAnswerAfterTheTimeLimit_EndsTheAttempt_WithoutAskingTheNetwork()
    {
        var order = await _h.CreateOrderAsync();
        var waiting = await _h.StartCardAsync(order, "4242 4242 4242 4242");
        _h.Time.Advance(TimeSpan.FromMinutes(11));

        var late = await _h.EnterCodeAsync(order, waiting);

        Assert.Equal((PaymentAttemptStatus.Expired, DeclineCodes.TimedOut), (late.Status, late.DeclineCode));
        Assert.Equal(0, _h.CardRail.CaptureCalls);
        Assert.Equal(OrderStatus.AwaitingFiatPayment, await StatusAsync(order));
    }

    [Fact]
    public async Task OneFailingItem_DoesNotStopTheSweep()
    {
        var bad = await _h.CreateOrderAsync();
        var good = await _h.CreateOrderAsync();
        var unknown = await _h.SeedApprovedAsync(bad, networkKnowsIt: false); // capturing it always fails
        var known = await _h.SeedApprovedAsync(good);
        _h.Time.Advance(TimeSpan.FromMinutes(3));

        Assert.Equal(new SweepResult(0, 1, 0, 0, 1), await _h.Processor.SweepAsync(None));

        Assert.Equal(PaymentAttemptStatus.Captured, (await _h.Attempts.FindAsync(known.AttemptId, None))!.Status);
        Assert.Equal(PaymentAttemptStatus.Authorized, (await _h.Attempts.FindAsync(unknown.AttemptId, None))!.Status);
        Assert.Contains(_h.Logs.Lines, line => line.StartsWith("Error", StringComparison.Ordinal) && line.Contains(unknown.AttemptId.ToString(), StringComparison.Ordinal));
    }

    // ---- locks ----

    [Fact]
    public async Task ThePaymentLock_IsHeldWhileTheNetworkIsAsked_AndLetGoBeforeTheOrderIsTold()
    {
        var order = await _h.CreateOrderAsync();
        var paymentLock = OrderLockKeys.Payment(order.OrderId);
        var whileStarting = new List<bool>();
        var whileCapturing = new List<bool>();
        var whileBuying = new List<(bool PaymentLock, bool OrderLock)>();
        _h.CardRail.OnStart = () => whileStarting.Add(_h.Locks.IsHeld(paymentLock));
        _h.CardRail.OnCapture = () => whileCapturing.Add(_h.Locks.IsHeld(paymentLock));
        _h.Liquidity.OnBuy = _ => whileBuying.Add((_h.Locks.IsHeld(paymentLock), _h.Locks.IsHeld(order.OrderId)));

        await _h.StartCardAsync(order, "4111 1111 1111 1111"); // approved at once: captured while starting

        Assert.Equal([true], whileStarting);
        Assert.Equal([true], whileCapturing);
        Assert.Equal([(false, true)], whileBuying); // the order flow runs under the order's lock alone

        // A payment that waits for the customer's code is captured when the code is answered, under the same lock.
        var second = await _h.CreateOrderAsync();
        var secondLock = OrderLockKeys.Payment(second.OrderId);
        _h.CardRail.OnCapture = () => whileCapturing.Add(_h.Locks.IsHeld(secondLock));
        await _h.EnterCodeAsync(second, await _h.StartCardAsync(second, "4242 4242 4242 4242"));

        Assert.Equal([true, true], whileCapturing);
    }

    [Fact]
    public async Task NoFlow_HoldsAnOrdersPaymentLockAndItsOwnLockTogether()
    {
        var typed = await _h.CreateOrderAsync();
        await _h.EnterCodeAsync(typed, await _h.StartCardAsync(typed, "4242 4242 4242 4242"));

        var failing = await _h.CreateOrderAsync();
        _h.UpiRail.FailCaptures = 1;
        await _h.ApproveAsync(failing, await _h.StartUpiAsync(failing));
        _h.Time.Advance(TimeSpan.FromMinutes(3));
        await _h.Processor.SweepAsync(None); // captures it

        var unrecorded = await _h.CreateOrderAsync();
        await _h.SeedCapturedAsync(unrecorded);
        await _h.Processor.SweepAsync(None); // records it

        // The refund is asked for while the order's lock is held: the provider must not reach for the payment lock.
        var held = await _h.CreateOrderAsync(6_000m, "USD");
        await _h.Processor.PayWithTestUpiAsync(held.OrderId, None);
        var reviews = _h.Services.GetRequiredService<FiatCryptoGateway.Application.Compliance.ComplianceReviewService>();
        await reviews.ReviewAsync(held.OrderId, ReviewActionKind.Reject, "analyst-1", "Source of funds not explained.", null, None);
        await reviews.ReviewAsync(held.OrderId, ReviewActionKind.FreezeAndReport, "analyst-1", "Reported as suspicious.", "STR-2026-0001", None);
        await reviews.ReviewAsync(held.OrderId, ReviewActionKind.ReleaseForRefund, "analyst-1", "Release instructed in writing.", null, None);
        Assert.Equal(OrderStatus.Refunded, await StatusAsync(held));

        Assert.Empty(_h.Locks.Nested);
        Assert.All(
            new[] { typed, failing, held },
            order =>
            {
                Assert.True(_h.Locks.Acquisitions(OrderLockKeys.Payment(order.OrderId)) > 0); // so this is not a test of nothing
                Assert.True(_h.Locks.Acquisitions(order.OrderId) > 0);
            });
    }

    // ---- two customers at once, and what is never kept ----

    [Fact]
    public async Task TwoCustomersPayingTheSameOrderAtOnce_AreChargedOnce()
    {
        for (var round = 0; round < 25; round++)
        {
            var order = await _h.CreateOrderAsync();
            var capturesBefore = _h.UpiRail.CaptureCalls;

            var outcomes = await Task.WhenAll(Task.Run(() => TryPayAsync(order)), Task.Run(() => TryPayAsync(order)));

            // Whatever the interleaving, the second customer is turned away or finds their attempt ended, never charged.
            Assert.All(outcomes.OfType<GatewayRuleException>(), refusal => Assert.Contains(refusal.Code, new[] { "ORDER_NOT_PAYABLE", "ORDER_ALREADY_PAID", "PAYMENT_IN_PROGRESS" }));
            Assert.Single(await _h.Attempts.ListForOrderAsync(order.OrderId, None), attempt => attempt.WasCaptured);
            Assert.Equal(1, _h.UpiRail.CaptureCalls - capturesBefore);
            Assert.Equal(1, await _h.LedgerEntriesAsync(order, EntryKinds.FiatReceived));
        }
    }

    [Fact]
    public async Task WhatTheCustomerTyped_IsNeverKeptOrLogged()
    {
        const string Number = "378282246310005";
        const string SecurityCode = "7391";
        const string Holder = "Zebediah Quillfeather";
        var order = await _h.CreateOrderAsync();
        _h.CardRail.FailCaptures = 1; // so the warning and the sweep, which log, are exercised too

        var typed = new CardInput("3782 8224 6310 005", 12, 2028, SecurityCode, Holder);
        var approved = await _h.Processor.StartAsync(order.OrderId, typed, None);
        _h.Time.Advance(TimeSpan.FromMinutes(3));
        await _h.Processor.SweepAsync(None);

        Assert.Equal(PaymentAttemptStatus.Authorized, approved.Status);
        var stored = await _h.StoredAsync(approved);
        Assert.Equal(("American Express ····0005", "American Express", "0005"), (stored.Instrument.Label, stored.Instrument.Brand, stored.Instrument.Last4));

        // Identifiers are random hex, which can hold any short run of digits, so they are taken out before looking.
        var everything = Regex.Replace(
            string.Join('\n', _h.Logs.Lines) + JsonSerializer.Serialize(await _h.Attempts.ListForOrderAsync(order.OrderId, None)),
            "[0-9a-fA-F]{8}(-?[0-9a-fA-F]{4}){3}-?[0-9a-fA-F]{12}",
            string.Empty,
            RegexOptions.CultureInvariant);
        Assert.NotEmpty(_h.Logs.Lines);
        foreach (var secret in new[] { Number, "3782 8224 6310 005", "37828224631", SecurityCode, Holder, "Zebediah" })
        {
            Assert.DoesNotContain(secret, everything, StringComparison.Ordinal);
        }

        Assert.Equal("CardInput (details hidden)", typed.ToString());
    }

    private async Task<OrderStatus> StatusAsync(PaymentOrder order) => (await _h.Orders.FindAsync(order.OrderId, None))!.Status;

    private async Task<Exception?> TryPayAsync(PaymentOrder order)
    {
        try
        {
            await _h.Processor.PayWithTestUpiAsync(order.OrderId, None);
            return null;
        }
        catch (GatewayRuleException exception)
        {
            return exception;
        }
    }
}
