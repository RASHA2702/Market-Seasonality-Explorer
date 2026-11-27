using System.Text.RegularExpressions;
using FiatCryptoGateway.Domain.Assets;

namespace FiatCryptoGateway.Domain.Payments;

public enum PaymentAttemptStatus
{
    /// <summary>Registered, before the payment network has answered.</summary>
    Started,

    /// <summary>The customer must still prove it is them: a one-time password, or approval in their UPI app.</summary>
    AwaitingAuthentication,

    /// <summary>The network approved the payment; the money is held but not yet taken.</summary>
    Authorized,

    Captured,
    Declined,
    AuthenticationFailed,
    Expired,

    /// <summary>The network could not be reached or failed. The customer was not charged.</summary>
    Failed,

    Refunded,
}

public enum ChallengeKind
{
    /// <summary>A one-time password sent to the customer.</summary>
    Otp,

    /// <summary>The customer approves a UPI collect request in their UPI app.</summary>
    UpiApproval,
}

public sealed record AttemptEvent(int Sequence, PaymentAttemptStatus? From, PaymentAttemptStatus To, DateTimeOffset OccurredAt, string Reason);

public sealed record AuthenticationChallenge(ChallengeKind Kind, int MaxTries, int TriesUsed)
{
    public int TriesLeft => Math.Max(0, MaxTries - TriesUsed);
}

/// <summary>Why an attempt did not succeed. Stable codes, never free text.</summary>
public static class DeclineCodes
{
    public const string DoNotHonor = "DO_NOT_HONOR";
    public const string InsufficientFunds = "INSUFFICIENT_FUNDS";
    public const string ExpiredCard = "EXPIRED_CARD";
    public const string IncorrectSecurityCode = "INCORRECT_SECURITY_CODE";
    public const string InvalidUpiAddress = "INVALID_UPI_ADDRESS";
    public const string CustomerDeclined = "CUSTOMER_DECLINED";
    public const string AuthenticationFailed = "AUTHENTICATION_FAILED";
    public const string RailUnavailable = "RAIL_UNAVAILABLE";
    public const string TimedOut = "TIMED_OUT";

    /// <summary>The customer started another attempt for the same order.</summary>
    public const string Superseded = "SUPERSEDED";

    /// <summary>The order's payment deadline passed before the payment was completed.</summary>
    public const string OrderClosed = "ORDER_CLOSED";

    /// <summary>
    /// The network approved the payment but it could not be captured for so long that the approval was given up. Needs a
    /// person to check the network's own report, since the gateway cannot be sure the customer was not charged.
    /// </summary>
    public const string AuthorizationLapsed = "AUTHORIZATION_LAPSED";
}

/// <summary>
/// One try by a customer to pay an order. It moves through a fixed set of steps (a started attempt is answered by the
/// payment network, may need the customer to authenticate, is authorised and then captured) and every step is kept in
/// an append-only history. An order can have several attempts, but at most one is ever captured. Nothing here can hold
/// a card number: see <see cref="PaymentInstrument"/>.
/// </summary>
public sealed partial class PaymentAttempt
{
    private static readonly Dictionary<PaymentAttemptStatus, PaymentAttemptStatus[]> Legal =
        new()
        {
            [PaymentAttemptStatus.Started] =
            [
                PaymentAttemptStatus.AwaitingAuthentication, PaymentAttemptStatus.Authorized, PaymentAttemptStatus.Declined,
                PaymentAttemptStatus.Failed, PaymentAttemptStatus.Expired,
            ],
            [PaymentAttemptStatus.AwaitingAuthentication] =
            [
                PaymentAttemptStatus.Authorized, PaymentAttemptStatus.AuthenticationFailed, PaymentAttemptStatus.Declined,
                PaymentAttemptStatus.Failed, PaymentAttemptStatus.Expired,
            ],
            [PaymentAttemptStatus.Authorized] = [PaymentAttemptStatus.Captured, PaymentAttemptStatus.Failed],
            [PaymentAttemptStatus.Captured] = [PaymentAttemptStatus.Refunded],
        };

    private readonly List<AttemptEvent> _events = [];

    private PaymentAttempt(
        Guid attemptId,
        Guid orderId,
        string clientId,
        Money amount,
        PaymentInstrument instrument,
        PaymentAttemptStatus status,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt)
    {
        AttemptId = attemptId;
        OrderId = orderId;
        ClientId = clientId;
        Amount = amount;
        Instrument = instrument;
        Status = status;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
        ExpiresAt = expiresAt;
    }

    public Guid AttemptId { get; }

    public Guid OrderId { get; }

    public string ClientId { get; }

    public PaymentMethod Method => Instrument.Method;

    public Money Amount { get; }

    public PaymentInstrument Instrument { get; }

    public PaymentAttemptStatus Status { get; private set; }

    /// <summary>The payment network's own reference for this payment (opaque to us).</summary>
    public string? RailReference { get; private set; }

    public AuthenticationChallenge? Challenge { get; private set; }

    public string? DeclineCode { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Until when the customer may complete authentication.</summary>
    public DateTimeOffset ExpiresAt { get; }

    public DateTimeOffset? CapturedAt { get; private set; }

    /// <summary>Our ID for the captured payment: what the order and the ledger refer to, and the daily report lists.</summary>
    public string? ProviderPaymentId { get; private set; }

    /// <summary>When the captured payment was recorded on the order. Null while that is still to do.</summary>
    public DateTimeOffset? RecordedOnOrderAt { get; private set; }

    public string? RefundId { get; private set; }

    public DateTimeOffset? RefundedAt { get; private set; }

    public IReadOnlyList<AttemptEvent> Events => _events.AsReadOnly();

    /// <summary>The stored version this copy was loaded at, used to refuse saving over a newer change.</summary>
    public int Version { get; internal set; }

    public bool IsPending => Status is PaymentAttemptStatus.Started or PaymentAttemptStatus.AwaitingAuthentication;

    /// <summary>Money was taken: captured, or later refunded.</summary>
    public bool WasCaptured => Status is PaymentAttemptStatus.Captured or PaymentAttemptStatus.Refunded;

    public bool IsExpiredAt(DateTimeOffset now) => IsPending && now >= ExpiresAt;

    public static PaymentAttempt Start(
        Guid attemptId,
        Guid orderId,
        string clientId,
        Money amount,
        PaymentInstrument instrument,
        DateTimeOffset now,
        TimeSpan authenticationWindow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentNullException.ThrowIfNull(amount);
        ArgumentNullException.ThrowIfNull(instrument);
        if (!amount.IsPositive)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "A payment is for a positive amount.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(authenticationWindow, TimeSpan.Zero);
        var attempt = new PaymentAttempt(attemptId, orderId, clientId, amount, instrument, PaymentAttemptStatus.Started, now, now + authenticationWindow);
        attempt._events.Add(new AttemptEvent(1, null, PaymentAttemptStatus.Started, now, "ATTEMPT_STARTED"));
        return attempt;
    }

    /// <summary>The network wants the customer to authenticate first.</summary>
    public void RequireAuthentication(string railReference, ChallengeKind kind, int maxTries, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(railReference);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTries, 1);
        MoveTo(PaymentAttemptStatus.AwaitingAuthentication, "AUTHENTICATION_REQUIRED", now);
        RailReference = railReference;
        Challenge = new AuthenticationChallenge(kind, maxTries, TriesUsed: 0);
    }

    /// <summary>
    /// The customer's answer was wrong. Returns true if they may try again; after the last try the attempt fails.
    /// </summary>
    public bool RejectAuthentication(DateTimeOffset now)
    {
        var challenge = Challenge ?? throw new InvalidOperationException("The attempt is not waiting for authentication.");
        EnsureStatus(PaymentAttemptStatus.AwaitingAuthentication);
        challenge = challenge with { TriesUsed = challenge.TriesUsed + 1 };
        Challenge = challenge;
        UpdatedAt = now;
        if (challenge.TriesLeft > 0)
        {
            return true;
        }

        FailAuthentication(DeclineCodes.AuthenticationFailed, now);
        return false;
    }

    /// <summary>The customer gave up or did not pass authentication.</summary>
    public void FailAuthentication(string code, DateTimeOffset now)
    {
        MoveTo(PaymentAttemptStatus.AuthenticationFailed, Code(code), now);
        DeclineCode = code;
    }

    /// <summary>The network approved the payment, with or without authentication first.</summary>
    public void Authorize(string railReference, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(railReference);
        MoveTo(PaymentAttemptStatus.Authorized, "AUTHORIZED", now);
        RailReference = railReference;
    }

    /// <summary>The bank or network refused the payment.</summary>
    public void Decline(string code, DateTimeOffset now)
    {
        MoveTo(PaymentAttemptStatus.Declined, Code(code), now);
        DeclineCode = code;
    }

    /// <summary>The network could not be reached or failed. The customer was not charged.</summary>
    public void Fail(string code, DateTimeOffset now)
    {
        MoveTo(PaymentAttemptStatus.Failed, Code(code), now);
        DeclineCode = code;
    }

    public void Expire(string code, DateTimeOffset now)
    {
        MoveTo(PaymentAttemptStatus.Expired, Code(code), now);
        DeclineCode = code;
    }

    public void Capture(string providerPaymentId, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerPaymentId);
        MoveTo(PaymentAttemptStatus.Captured, "CAPTURED", now);
        ProviderPaymentId = providerPaymentId;
        CapturedAt = now;
    }

    /// <summary>The captured payment is now on the order and in the ledger.</summary>
    public void MarkRecordedOnOrder(DateTimeOffset now)
    {
        if (!WasCaptured)
        {
            throw new InvalidOperationException("Only a captured payment is recorded on an order.");
        }

        RecordedOnOrderAt ??= now;
        UpdatedAt = now;
    }

    public void Refund(string refundId, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refundId);
        MoveTo(PaymentAttemptStatus.Refunded, "REFUNDED", now);
        RefundId = refundId;
        RefundedAt = now;
    }

    /// <summary>An independent copy, for stores that keep attempts in memory.</summary>
    public PaymentAttempt Copy() => Restore(
        AttemptId, OrderId, ClientId, Amount, Instrument, Status, RailReference, Challenge, DeclineCode, CreatedAt, UpdatedAt, ExpiresAt,
        CapturedAt, ProviderPaymentId, RecordedOnOrderAt, RefundId, RefundedAt, Version, _events);

    internal static PaymentAttempt Restore(
        Guid attemptId,
        Guid orderId,
        string clientId,
        Money amount,
        PaymentInstrument instrument,
        PaymentAttemptStatus status,
        string? railReference,
        AuthenticationChallenge? challenge,
        string? declineCode,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        DateTimeOffset expiresAt,
        DateTimeOffset? capturedAt,
        string? providerPaymentId,
        DateTimeOffset? recordedOnOrderAt,
        string? refundId,
        DateTimeOffset? refundedAt,
        int version,
        IEnumerable<AttemptEvent> events)
    {
        var attempt = new PaymentAttempt(attemptId, orderId, clientId, amount, instrument, status, createdAt, expiresAt)
        {
            RailReference = railReference,
            Challenge = challenge,
            DeclineCode = declineCode,
            UpdatedAt = updatedAt,
            CapturedAt = capturedAt,
            ProviderPaymentId = providerPaymentId,
            RecordedOnOrderAt = recordedOnOrderAt,
            RefundId = refundId,
            RefundedAt = refundedAt,
            Version = version,
        };
        var history = events.OrderBy(entry => entry.Sequence).ToList();

        // The status and the history are saved together, so they always agree. If they do not, what was read mixes two
        // moments (or the record is damaged), and acting on it could be wrong: refuse it loudly.
        if (history.Count == 0 || history[^1].To != status)
        {
            throw new InvalidOperationException(
                $"Payment attempt {attemptId} has status {status} but its history ends at {(history.Count == 0 ? "nothing" : history[^1].To)}.");
        }

        attempt._events.AddRange(history);
        return attempt;
    }

    [GeneratedRegex("^[A-Z][A-Z0-9_]{1,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();

    /// <summary>Codes are stable words, never free text, so they can never carry a card number.</summary>
    private static string Code(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        return CodePattern().IsMatch(code) ? code : throw new ArgumentException("A code is upper-case words joined by underscores.", nameof(code));
    }

    private void EnsureStatus(PaymentAttemptStatus expected)
    {
        if (Status != expected)
        {
            throw new InvalidOperationException($"The attempt is {Status}, not {expected}.");
        }
    }

    private void MoveTo(PaymentAttemptStatus next, string reason, DateTimeOffset now)
    {
        if (!Legal.TryGetValue(Status, out var allowed) || !allowed.Contains(next))
        {
            throw new InvalidOperationException($"A payment attempt cannot go from {Status} to {next}.");
        }

        _events.Add(new AttemptEvent(_events.Count + 1, Status, next, now, reason));
        Status = next;
        UpdatedAt = now;
    }
}
