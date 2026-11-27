using FiatCryptoGateway.Domain.Assets;
using FiatCryptoGateway.Domain.Payments;

namespace FiatCryptoGateway.Application.Payments;

/// <summary>
/// What a customer typed to pay. It is passed to the payment network once and never stored, logged or put in an
/// address: <c>ToString</c> hides it, so even an accidental log line cannot print it.
/// </summary>
public abstract class PaymentInstrumentInput
{
    public abstract PaymentMethod Method { get; }

    public sealed override string ToString() => $"{GetType().Name} (details hidden)";
}

public sealed class CardInput(string number, int expiryMonth, int expiryYear, string securityCode, string? holderName) : PaymentInstrumentInput
{
    public override PaymentMethod Method => PaymentMethod.Card;

    public string Number { get; } = number;

    public int ExpiryMonth { get; } = expiryMonth;

    /// <summary>Four digits, for example 2029.</summary>
    public int ExpiryYear { get; } = expiryYear;

    public string SecurityCode { get; } = securityCode;

    public string? HolderName { get; } = holderName;
}

public sealed class UpiInput(string address) : PaymentInstrumentInput
{
    public override PaymentMethod Method => PaymentMethod.Upi;

    public string Address { get; } = address;
}

/// <summary>The customer's answer to an authentication step.</summary>
public abstract class AuthenticationResponse
{
    public sealed override string ToString() => $"{GetType().Name} (hidden)";
}

public sealed class OtpResponse(string code) : AuthenticationResponse
{
    public string Code { get; } = code;
}

public sealed class ApprovalResponse(bool approved) : AuthenticationResponse
{
    public bool Approved { get; } = approved;
}

/// <summary>Whether a rail accepts what the customer typed, before anything is stored or sent.</summary>
public sealed record InstrumentCheck(PaymentInstrument? Instrument, string? RefusalCode, string? RefusalMessage)
{
    public bool Accepted => Instrument is not null;

    public static InstrumentCheck Accept(PaymentInstrument instrument) => new(instrument, null, null);

    public static InstrumentCheck Refuse(string code, string message) => new(null, code, message);
}

public enum RailOutcome
{
    Approved,
    Declined,
    ChallengeRequired,

    /// <summary>The customer's answer was wrong; they may try again.</summary>
    AuthenticationRejected,

    /// <summary>The network failed or could not be reached. The customer was not charged.</summary>
    Failed,
}

public sealed record RailResponse(RailOutcome Outcome, string? RailReference = null, string? Code = null, ChallengeKind? Challenge = null)
{
    public static RailResponse Approved(string railReference) => new(RailOutcome.Approved, railReference);

    public static RailResponse Declined(string code) => new(RailOutcome.Declined, Code: code);

    public static RailResponse ChallengeRequired(string railReference, ChallengeKind kind) => new(RailOutcome.ChallengeRequired, railReference, Challenge: kind);

    public static RailResponse Rejected() => new(RailOutcome.AuthenticationRejected);

    public static RailResponse Failed(string code) => new(RailOutcome.Failed, Code: code);
}

public sealed class RailPaymentRequest(Guid attemptId, Guid orderId, Money amount, PaymentInstrumentInput input)
{
    public Guid AttemptId { get; } = attemptId;

    public Guid OrderId { get; } = orderId;

    public Money Amount { get; } = amount;

    public PaymentInstrumentInput Input { get; } = input;
}

/// <summary>
/// One payment network the processor can use, for one payment method: a card network, UPI, a bank. The sandbox has a
/// simulated rail for each. Going live means writing a real adapter for this interface, and getting the agreements and
/// licences that need; the processor does not change.
/// </summary>
public interface IPaymentRail
{
    string Name { get; }

    PaymentMethod Method { get; }

    /// <summary>
    /// True for a simulated rail. The API refuses to start if any rail is not a sandbox one, as it already refuses any
    /// mode but Sandbox.
    /// </summary>
    bool IsSandbox { get; }

    /// <summary>
    /// Whether this rail accepts what the customer typed, and how to describe it without keeping it. Called before
    /// anything is stored or sent. A sandbox rail accepts test instruments only.
    /// </summary>
    InstrumentCheck Check(PaymentInstrumentInput input, DateTimeOffset now);

    /// <summary>Begins the payment: approved, declined, or waiting for the customer to authenticate.</summary>
    Task<RailResponse> StartAsync(RailPaymentRequest request, CancellationToken cancellationToken);

    Task<RailResponse> CompleteAuthenticationAsync(string railReference, AuthenticationResponse response, CancellationToken cancellationToken);

    /// <summary>Takes the authorised money. Idempotent per <paramref name="railReference"/>; returns when it was taken.</summary>
    Task<DateTimeOffset> CaptureAsync(string railReference, Money amount, CancellationToken cancellationToken);

    /// <summary>Returns captured money. Idempotent per <paramref name="idempotencyKey"/>; returns when it was returned.</summary>
    Task<DateTimeOffset> RefundAsync(string railReference, Money amount, string idempotencyKey, CancellationToken cancellationToken);
}

public sealed class PaymentAttemptConcurrencyException : Exception
{
    public PaymentAttemptConcurrencyException()
        : base("The payment attempt changed since it was loaded.")
    {
    }

    public PaymentAttemptConcurrencyException(string message)
        : base(message)
    {
    }

    public PaymentAttemptConcurrencyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The processor's own records: every attempt, with its history. Nothing here can hold a card number.
/// Saving fails in two different ways. <see cref="PaymentAttemptConcurrencyException"/> is a lost race (the attempt
/// changed since it was loaded, or the order already has a captured payment), which callers expect and handle. An
/// <see cref="InvalidOperationException"/> means an attempt, payment or refund identifier was already used by another
/// attempt: a programming error, never retried and never treated as a race.
/// </summary>
public interface IPaymentAttemptStore
{
    /// <summary>Stores a new attempt with its history.</summary>
    Task AddAsync(PaymentAttempt attempt, CancellationToken cancellationToken);

    /// <summary>Saves a changed attempt with its new history. Throws <see cref="PaymentAttemptConcurrencyException"/> if it changed since it was loaded.</summary>
    Task SaveAsync(PaymentAttempt attempt, CancellationToken cancellationToken);

    Task<PaymentAttempt?> FindAsync(Guid attemptId, CancellationToken cancellationToken);

    /// <summary>The order's attempts, oldest first.</summary>
    Task<IReadOnlyList<PaymentAttempt>> ListForOrderAsync(Guid orderId, CancellationToken cancellationToken);

    /// <summary>The order's captured (or since refunded) attempt, if there is one.</summary>
    Task<PaymentAttempt?> FindCapturedForOrderAsync(Guid orderId, CancellationToken cancellationToken);

    /// <summary>Captured payments not yet recorded on their order, oldest first.</summary>
    Task<IReadOnlyList<PaymentAttempt>> ListCapturedNotRecordedAsync(int limit, CancellationToken cancellationToken);

    /// <summary>Attempts still waiting that have passed their time limit, oldest first.</summary>
    Task<IReadOnlyList<PaymentAttempt>> ListPendingExpiredAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken);

    /// <summary>Attempts captured or refunded within [<paramref name="windowStart"/>, <paramref name="windowEnd"/>), for the daily settlement report.</summary>
    Task<IReadOnlyList<PaymentAttempt>> ListSettledBetweenAsync(DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken);
}
