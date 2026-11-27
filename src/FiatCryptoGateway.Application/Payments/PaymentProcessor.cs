using System.Diagnostics.CodeAnalysis;
using FiatCryptoGateway.Application.OnRamp;
using FiatCryptoGateway.Application.Ports;
using FiatCryptoGateway.Domain;
using FiatCryptoGateway.Domain.Orders;
using FiatCryptoGateway.Domain.Payments;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FiatCryptoGateway.Application.Payments;

/// <summary>
/// What a customer or a checkout page may know about an attempt. Never a payment network's reference or the gateway's
/// payment identifier, and never anything the customer typed beyond the brand and last four digits (or UPI address).
/// </summary>
public sealed record PaymentAttemptView(
    Guid AttemptId,
    Guid OrderId,
    PaymentMethod Method,
    PaymentAttemptStatus Status,
    string InstrumentLabel,
    ChallengeKind? Challenge,
    int? TriesLeft,
    string? DeclineCode,
    DateTimeOffset ExpiresAt)
{
    public static PaymentAttemptView From(PaymentAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        return new PaymentAttemptView(
            attempt.AttemptId,
            attempt.OrderId,
            attempt.Method,
            attempt.Status,
            attempt.Instrument.Label,
            attempt.Status == PaymentAttemptStatus.AwaitingAuthentication ? attempt.Challenge?.Kind : null,
            attempt.Status == PaymentAttemptStatus.AwaitingAuthentication ? attempt.Challenge?.TriesLeft : null,
            attempt.DeclineCode,
            attempt.ExpiresAt);
    }

    /// <summary>Whether the customer still has something to do: type a code, or wait for the bank.</summary>
    public bool IsPending => Status is PaymentAttemptStatus.Started or PaymentAttemptStatus.AwaitingAuthentication or PaymentAttemptStatus.Authorized;

    /// <summary>The money was taken.</summary>
    public bool IsPaid => Status is PaymentAttemptStatus.Captured or PaymentAttemptStatus.Refunded;
}

/// <summary>
/// What one background sweep did: attempts ended for running out of time, approved payments captured, captured payments
/// recorded on their orders, approvals given up after they could not be captured for too long, and items that failed
/// and will be retried.
/// </summary>
public sealed record SweepResult(int Expired, int Captured, int Recorded, int Lapsed, int Failed)
{
    public int Total => Expired + Captured + Recorded + Lapsed + Failed;
}

/// <summary>
/// The gateway's own payment processor: takes what a customer typed, asks the right payment network, handles the bank's
/// check, captures the money and tells the order. Every step is saved before the next outside call, so a crash at any
/// moment leaves an attempt in a state the background sweep knows how to finish.
/// </summary>
/// <remarks>
/// Locking: starting, answering and capturing hold the order's <em>payment</em> lock. Recording the payment on the order
/// takes the order's own lock inside <see cref="OnRampService"/>. This class never holds both, and the provider's refund
/// (called while the order lock is held) takes no payment lock, so the two locks can never wait on each other.
/// </remarks>
public sealed partial class PaymentProcessor(
    IPaymentAttemptStore attempts,
    IOrderStore orders,
    OnRampService onRamp,
    PaymentRailSet rails,
    IOrderLocks locks,
    IOptions<ProcessorOptions> options,
    TimeProvider time,
    ILogger<PaymentProcessor> logger)
{
    /// <summary>
    /// Begins a payment for an order with what the customer typed. The details are checked, passed to the network once and
    /// never kept. Throws <see cref="GatewayRuleException"/> if the order cannot be paid, or the network refuses what was typed.
    /// </summary>
    public async Task<PaymentAttemptView> StartAsync(Guid orderId, PaymentInstrumentInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var rail = rails.Require(input.Method);
        var order = await LoadOrderAsync(orderId, cancellationToken);
        if (!IsPayable(order, time.GetUtcNow()))
        {
            throw new GatewayRuleException("ORDER_NOT_PAYABLE", "This order can no longer be paid.");
        }

        // What the customer typed is judged before anything is stored or locked. A refusal is not an attempt.
        var check = rail.Check(input, time.GetUtcNow());
        if (!check.Accepted)
        {
            throw new GatewayRuleException(check.RefusalCode!, check.RefusalMessage!);
        }

        PaymentAttempt attempt;
        await using (await locks.AcquireAsync(OrderLockKeys.Payment(orderId), cancellationToken))
        {
            var now = time.GetUtcNow();
            order = await LoadOrderAsync(orderId, cancellationToken);
            if (!IsPayable(order, now))
            {
                throw new GatewayRuleException("ORDER_NOT_PAYABLE", "This order can no longer be paid.");
            }

            var existing = await attempts.ListForOrderAsync(orderId, cancellationToken);
            if (existing.Any(other => other.WasCaptured))
            {
                throw new GatewayRuleException("ORDER_ALREADY_PAID", "This order has already been paid.");
            }

            // An approved payment is being completed. A second attempt now could take the money twice.
            if (existing.Any(other => other.Status == PaymentAttemptStatus.Authorized))
            {
                throw new GatewayRuleException("PAYMENT_IN_PROGRESS", "A payment for this order is being completed. Please wait a moment.");
            }

            if (existing.Count >= options.Value.MaxAttemptsPerOrder)
            {
                throw new GatewayRuleException("TOO_MANY_ATTEMPTS", "There have been too many payment attempts for this order. Please start again from the merchant.");
            }

            // One live attempt per order: starting another ends the one still waiting.
            foreach (var waiting in existing.Where(other => other.IsPending))
            {
                waiting.Expire(DeclineCodes.Superseded, now);
                await attempts.SaveAsync(waiting, cancellationToken);
            }

            var window = TimeSpan.FromMinutes(options.Value.AttemptWindowMinutes);
            var untilDeadline = order.PaymentDeadline - now;
            attempt = PaymentAttempt.Start(
                Guid.NewGuid(), orderId, order.ClientId, order.Quote.SourceAmount, check.Instrument!, now, untilDeadline < window ? untilDeadline : window);
            await attempts.AddAsync(attempt, cancellationToken);

            var response = await rail.StartAsync(new RailPaymentRequest(attempt.AttemptId, orderId, attempt.Amount, input), cancellationToken);
            attempt = await ApplyAsync(attempt, rail, response, cancellationToken);
        }

        await RecordAfterCaptureAsync(attempt, cancellationToken);
        return PaymentAttemptView.From(attempt);
    }

    /// <summary>
    /// The customer's answer to their bank's check (a one-time code, or approval in their bank app). Answering an
    /// attempt that is no longer waiting just reports where it stands.
    /// </summary>
    public async Task<PaymentAttemptView> AuthenticateAsync(
        Guid orderId, Guid attemptId, AuthenticationResponse response, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        PaymentAttempt attempt;
        await using (await locks.AcquireAsync(OrderLockKeys.Payment(orderId), cancellationToken))
        {
            var found = await attempts.FindAsync(attemptId, cancellationToken);
            attempt = found is not null && found.OrderId == orderId
                ? found
                : throw new ResourceNotFoundException("ATTEMPT_NOT_FOUND", "That payment attempt does not exist.");
            if (attempt.Status != PaymentAttemptStatus.AwaitingAuthentication)
            {
                return PaymentAttemptView.From(attempt);
            }

            var now = time.GetUtcNow();
            if (attempt.IsExpiredAt(now))
            {
                attempt.Expire(DeclineCodes.TimedOut, now);
                await attempts.SaveAsync(attempt, cancellationToken);
                return PaymentAttemptView.From(attempt);
            }

            var order = await orders.FindAsync(orderId, cancellationToken);
            if (order is null || !IsPayable(order, now))
            {
                attempt.Expire(DeclineCodes.OrderClosed, now);
                await attempts.SaveAsync(attempt, cancellationToken);
                return PaymentAttemptView.From(attempt);
            }

            var rail = rails.Require(attempt.Method);
            var answer = await rail.CompleteAuthenticationAsync(attempt.RailReference!, response, cancellationToken);
            attempt = await ApplyAsync(attempt, rail, answer, cancellationToken);
        }

        await RecordAfterCaptureAsync(attempt, cancellationToken);
        return PaymentAttemptView.From(attempt);
    }

    /// <summary>One attempt of an order, or null if it is not that order's.</summary>
    public async Task<PaymentAttemptView?> FindAttemptAsync(Guid orderId, Guid attemptId, CancellationToken cancellationToken)
    {
        var attempt = await attempts.FindAsync(attemptId, cancellationToken);
        return attempt is not null && attempt.OrderId == orderId ? PaymentAttemptView.From(attempt) : null;
    }

    /// <summary>The order's most recent attempt, or null if nobody has tried to pay yet.</summary>
    public async Task<PaymentAttemptView?> LatestAttemptAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var all = await attempts.ListForOrderAsync(orderId, cancellationToken);
        return all.Count == 0 ? null : PaymentAttemptView.From(all[^1]);
    }

    /// <summary>
    /// Finishes what a crash or an outage left half done: ends attempts that ran out of time, captures payments the
    /// network approved, and records captured payments on their orders. Every step is safe to repeat, and one failing
    /// item never stops the others.
    /// </summary>
    public async Task<SweepResult> SweepAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var batch = options.Value.SweepBatchSize;
        int expired = 0, captured = 0, recorded = 0, lapsed = 0, failed = 0;

        void Tally(Swept result, ref int done)
        {
            switch (result)
            {
                case Swept.Done:
                    done++;
                    break;
                case Swept.Lapsed:
                    lapsed++;
                    break;
                case Swept.Failed:
                    failed++;
                    break;
            }
        }

        foreach (var due in await attempts.ListPendingExpiredAsync(now, batch, cancellationToken))
        {
            Tally(await SweepOneAsync(due, ExpireAsync, cancellationToken), ref expired);
        }

        var stuckBefore = now - TimeSpan.FromSeconds(options.Value.StuckAfterSeconds);
        foreach (var stuck in await attempts.ListStuckAuthorizedAsync(stuckBefore, batch, cancellationToken))
        {
            Tally(await SweepOneAsync(stuck, FinishCaptureAsync, cancellationToken), ref captured);
        }

        foreach (var unrecorded in await attempts.ListCapturedNotRecordedAsync(batch, cancellationToken))
        {
            Tally(await SweepOneAsync(unrecorded, RecordCapturedAsync, cancellationToken), ref recorded);
        }

        return new SweepResult(expired, captured, recorded, lapsed, failed);
    }

    private enum Swept
    {
        /// <summary>Nothing to do: someone else already finished it, or it is not due yet.</summary>
        Skipped,
        Done,

        /// <summary>An approval that could not be captured for so long that it was given up.</summary>
        Lapsed,
        Failed,
    }

    private static bool IsPayable(PaymentOrder order, DateTimeOffset now) =>
        order.Status == OrderStatus.AwaitingFiatPayment && now < order.PaymentDeadline;

    private async Task<PaymentOrder> LoadOrderAsync(Guid orderId, CancellationToken cancellationToken) =>
        await orders.FindAsync(orderId, cancellationToken) ?? throw new ResourceNotFoundException("ORDER_NOT_FOUND", "The order does not exist.");

    /// <summary>
    /// Saves what the network answered and, if it approved, takes the money. Returns the attempt as it is now stored: if
    /// capturing failed part-way, that is the approved attempt the sweep will finish, not a half-changed copy.
    /// </summary>
    private async Task<PaymentAttempt> ApplyAsync(PaymentAttempt attempt, IPaymentRail rail, RailResponse response, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        switch (response.Outcome)
        {
            case RailOutcome.Approved:
                attempt.Authorize(
                    response.RailReference ?? throw new InvalidOperationException("A payment network approved a payment without a reference."), now);

                // The approval is saved before the money is taken, so a crash leaves an attempt the sweep can finish.
                await attempts.SaveAsync(attempt, cancellationToken);
                return await TryCaptureAsync(attempt, rail, cancellationToken);
            case RailOutcome.ChallengeRequired when attempt.Status == PaymentAttemptStatus.Started && response.Challenge is { } kind:
                attempt.RequireAuthentication(
                    response.RailReference ?? throw new InvalidOperationException("A payment network asked for a check without a reference."),
                    kind,
                    options.Value.AuthenticationTries,
                    now);
                break;
            case RailOutcome.AuthenticationRejected when attempt.Status == PaymentAttemptStatus.AwaitingAuthentication:
                attempt.RejectAuthentication(now);
                break;
            case RailOutcome.Declined:
                attempt.Decline(response.Code ?? DeclineCodes.DoNotHonor, now);
                break;
            default:
                // A failed network, or an answer that makes no sense at this step. The customer was not charged.
                attempt.Fail(response.Code ?? DeclineCodes.RailUnavailable, now);
                break;
        }

        await attempts.SaveAsync(attempt, cancellationToken);
        return attempt;
    }

    private async Task CaptureAsync(PaymentAttempt attempt, IPaymentRail rail, CancellationToken cancellationToken)
    {
        var capturedAt = await rail.CaptureAsync(attempt.RailReference!, attempt.Amount, cancellationToken);
        attempt.Capture(PaymentIdentifiers.Payment(attempt.AttemptId), capturedAt);
        await attempts.SaveAsync(attempt, cancellationToken);
    }

    /// <summary>
    /// Takes the money, and if that fails leaves the approved attempt for the sweep. The customer is told their payment is
    /// being completed, not shown an error for a payment that may well go through.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "The approval is saved and the sweep finishes the capture; the failure is logged, never lost.")]
    private async Task<PaymentAttempt> TryCaptureAsync(PaymentAttempt attempt, IPaymentRail rail, CancellationToken cancellationToken)
    {
        try
        {
            await CaptureAsync(attempt, rail, cancellationToken);
            return attempt;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogCaptureDeferred(logger, exception, attempt.AttemptId);

            // The copy in memory may be ahead of what is stored. Report what is stored.
            return await attempts.FindAsync(attempt.AttemptId, cancellationToken) ?? attempt;
        }
    }

    /// <summary>Tells the order about a captured payment, outside the payment lock. A failure here is the sweep's to retry.</summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "The payment is captured and saved; the sweep records it. The customer must not see an error for it.")]
    private async Task RecordAfterCaptureAsync(PaymentAttempt attempt, CancellationToken cancellationToken)
    {
        if (attempt.Status != PaymentAttemptStatus.Captured || attempt.RecordedOnOrderAt is not null)
        {
            return;
        }

        try
        {
            await RecordOnOrderAsync(attempt, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogRecordingDeferred(logger, exception, attempt.AttemptId);
        }
    }

    private async Task RecordOnOrderAsync(PaymentAttempt attempt, CancellationToken cancellationToken)
    {
        // A crash after the order took the payment but before this was marked leaves the order ahead of the attempt.
        var order = await orders.FindAsync(attempt.OrderId, cancellationToken);
        var alreadyOnOrder = order?.FiatPayment is { } payment && payment.ProviderPaymentId == attempt.ProviderPaymentId;
        if (!alreadyOnOrder)
        {
            var confirmation = new FiatPaymentConfirmation(
                GatewayProcessorProvider.ProviderName, attempt.ProviderPaymentId!, attempt.OrderId, attempt.Amount, attempt.CapturedAt!.Value);
            if (await onRamp.RecordFiatPaymentAsync(confirmation, cancellationToken) is null)
            {
                LogUnmatchedCapture(logger, attempt.AttemptId);
                return;
            }
        }

        await MarkRecordedAsync(attempt.AttemptId, cancellationToken);
    }

    private async Task MarkRecordedAsync(Guid attemptId, CancellationToken cancellationToken)
    {
        // Only the recorded-on-order time changes, so a copy that changed meanwhile (say, a refund) is simply reloaded.
        for (var tries = 0; tries < 3; tries++)
        {
            var current = await attempts.FindAsync(attemptId, cancellationToken);
            if (current is null || current.RecordedOnOrderAt is not null)
            {
                return;
            }

            current.MarkRecordedOnOrder(time.GetUtcNow());
            try
            {
                await attempts.SaveAsync(current, cancellationToken);
                return;
            }
            catch (PaymentAttemptConcurrencyException)
            {
                // Reload and try again.
            }
        }
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "One failing item is logged and counted, and the sweep goes on with the others.")]
    private async Task<Swept> SweepOneAsync(
        PaymentAttempt item, Func<PaymentAttempt, CancellationToken, Task<Swept>> step, CancellationToken cancellationToken)
    {
        try
        {
            return await step(item, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogSweepItemFailed(logger, exception, item.AttemptId);
            return Swept.Failed;
        }
    }

    private async Task<Swept> ExpireAsync(PaymentAttempt listed, CancellationToken cancellationToken)
    {
        await using (await locks.AcquireAsync(OrderLockKeys.Payment(listed.OrderId), cancellationToken))
        {
            var now = time.GetUtcNow();
            var current = await attempts.FindAsync(listed.AttemptId, cancellationToken);
            if (current is null || !current.IsExpiredAt(now))
            {
                return Swept.Skipped;
            }

            current.Expire(DeclineCodes.TimedOut, now);
            await attempts.SaveAsync(current, cancellationToken);
            return Swept.Done;
        }
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Any failure to capture is retried until the approval has lapsed; only then is it given up, logged and flagged.")]
    private async Task<Swept> FinishCaptureAsync(PaymentAttempt listed, CancellationToken cancellationToken)
    {
        PaymentAttempt attempt;
        await using (await locks.AcquireAsync(OrderLockKeys.Payment(listed.OrderId), cancellationToken))
        {
            var current = await attempts.FindAsync(listed.AttemptId, cancellationToken);
            if (current is null || current.Status != PaymentAttemptStatus.Authorized)
            {
                return Swept.Skipped;
            }

            attempt = current;
            var now = time.GetUtcNow();
            var lapse = TimeSpan.FromHours(options.Value.AuthorizationLapseHours);
            try
            {
                await CaptureAsync(attempt, rails.Require(attempt.Method), cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException && now - attempt.UpdatedAt >= lapse)
            {
                // Retried for the whole lapse period without success: stop, and leave it for a person to check against
                // the network's own report. The copy in memory may be ahead of what is stored, so reload it first.
                attempt = await attempts.FindAsync(attempt.AttemptId, cancellationToken) ?? attempt;
                attempt.Fail(DeclineCodes.AuthorizationLapsed, now);
                await attempts.SaveAsync(attempt, cancellationToken);
                LogAuthorizationLapsed(logger, attempt.AttemptId);
                return Swept.Lapsed;
            }
        }

        await RecordAfterCaptureAsync(attempt, cancellationToken);
        return Swept.Done;
    }

    private async Task<Swept> RecordCapturedAsync(PaymentAttempt listed, CancellationToken cancellationToken)
    {
        var current = await attempts.FindAsync(listed.AttemptId, cancellationToken);
        if (current is null || !current.WasCaptured || current.RecordedOnOrderAt is not null)
        {
            return Swept.Skipped;
        }

        await RecordOnOrderAsync(current, cancellationToken);
        return Swept.Done;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Capturing the approved payment {AttemptId} failed; the sweep will finish it.")]
    private static partial void LogCaptureDeferred(ILogger logger, Exception exception, Guid attemptId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recording the captured payment {AttemptId} on its order failed; the sweep will retry.")]
    private static partial void LogRecordingDeferred(ILogger logger, Exception exception, Guid attemptId);

    [LoggerMessage(Level = LogLevel.Error, Message = "The captured payment {AttemptId} matches no order. A person must check it and refund it by hand.")]
    private static partial void LogUnmatchedCapture(ILogger logger, Guid attemptId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Sweeping the payment attempt {AttemptId} failed; it will be retried.")]
    private static partial void LogSweepItemFailed(ILogger logger, Exception exception, Guid attemptId);

    [LoggerMessage(Level = LogLevel.Error, Message = "The approved payment {AttemptId} could not be captured and was given up. Check the network's own report for it.")]
    private static partial void LogAuthorizationLapsed(ILogger logger, Guid attemptId);
}
