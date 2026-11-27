using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using FiatCryptoGateway.Application.Payments;
using FiatCryptoGateway.Domain.Assets;
using FiatCryptoGateway.Domain.Payments;

namespace FiatCryptoGateway.Sandbox;

/// <summary>What a sandbox test card does when used to pay.</summary>
public enum CardScenario
{
    Approved,
    OtpThenApproved,
    OtpAlwaysRejected,
    DoNotHonor,
    InsufficientFunds,
    ExpiredCard,
    IncorrectSecurityCode,
    NetworkUnavailable,
}

public sealed record TestCard(string Number, string Brand, CardScenario Scenario, string Meaning)
{
    public int SecurityCodeLength => Brand == "American Express" ? 4 : 3;

    /// <summary>The number in groups of four, as people write it.</summary>
    public string Display => string.Join(' ', Enumerable.Range(0, (Number.Length + 3) / 4).Select(group => Number.Substring(group * 4, Math.Min(4, Number.Length - (group * 4)))));
}

public sealed record TestUpiAddress(string Address, string Meaning);

/// <summary>
/// The only instruments the sandbox accepts. They are the published test numbers payment providers use, so none is a
/// real card; any other number, even one that passes the card checksum, is refused before it is stored or sent.
/// </summary>
public static class SandboxTestInstruments
{
    /// <summary>The one-time password every sandbox authentication step accepts.</summary>
    public const string Otp = "123456";

    public const string UpiSuffix = "@sandbox";

    public static IReadOnlyList<TestCard> Cards { get; } =
    [
        new("4111111111111111", "Visa", CardScenario.Approved, "Approved, no authentication"),
        new("4242424242424242", "Visa", CardScenario.OtpThenApproved, "Asks for the one-time password, then approved"),
        new("5555555555554444", "Mastercard", CardScenario.OtpThenApproved, "Asks for the one-time password, then approved"),
        new("378282246310005", "American Express", CardScenario.Approved, "Approved (four-digit security code)"),
        new("4000000000003220", "Visa", CardScenario.OtpAlwaysRejected, "Asks for the one-time password, which is never accepted"),
        new("4000000000000002", "Visa", CardScenario.DoNotHonor, "Declined by the bank"),
        new("4000000000009995", "Visa", CardScenario.InsufficientFunds, "Declined: insufficient funds"),
        new("4000000000000069", "Visa", CardScenario.ExpiredCard, "Declined: expired card"),
        new("4000000000000127", "Visa", CardScenario.IncorrectSecurityCode, "Declined: incorrect security code"),
        new("4000000000000259", "Visa", CardScenario.NetworkUnavailable, "The card network is unavailable"),
    ];

    public static IReadOnlyList<TestUpiAddress> UpiAddresses { get; } =
    [
        new("success@sandbox", "You approve the request, and it is paid"),
        new("insufficient@sandbox", "You approve the request, and the bank declines: insufficient funds"),
        new("timeout@sandbox", "The request is never answered and times out"),
        new("invalid@sandbox", "The address does not exist"),
        new("unavailable@sandbox", "The UPI network is unavailable"),
    ];

    public static bool TryGetCard(string digits, out TestCard card)
    {
        card = Cards.FirstOrDefault(candidate => string.Equals(candidate.Number, digits, StringComparison.Ordinal))!;
        return card is not null;
    }
}

/// <summary>The sandbox's simulated card network, with the bank's side kept in memory (lost on restart).</summary>
public sealed class SandboxCardRail(TimeProvider time) : IPaymentRail
{
    private readonly ConcurrentDictionary<string, CardTransaction> _transactions = new(StringComparer.Ordinal);

    public string Name => "sandbox-card-network";

    public PaymentMethod Method => PaymentMethod.Card;

    public bool IsSandbox => true;

    public InstrumentCheck Check(PaymentInstrumentInput input, DateTimeOffset now)
    {
        if (input is not CardInput card)
        {
            return InstrumentCheck.Refuse("INVALID_INSTRUMENT", "Card details are needed to pay by card.");
        }

        // The card number is checked before anything else, so a real number is refused without being kept anywhere.
        var digits = CardNumbers.Normalize(card.Number);
        if (digits is null || !CardNumbers.PassesLuhn(digits))
        {
            return InstrumentCheck.Refuse("INVALID_CARD_NUMBER", "That card number is not valid. Check it and try again.");
        }

        if (!SandboxTestInstruments.TryGetCard(digits, out var testCard))
        {
            return InstrumentCheck.Refuse(
                "TEST_CARDS_ONLY", "This is a sandbox: only the listed test cards work. Your card was not accepted and nothing was kept.");
        }

        if (card.ExpiryMonth is < 1 or > 12 || card.ExpiryYear < now.Year || (card.ExpiryYear == now.Year && card.ExpiryMonth < now.Month) || card.ExpiryYear > now.Year + 20)
        {
            return InstrumentCheck.Refuse("INVALID_EXPIRY", "The expiry date is not valid. Use the month and year on the card, in the future.");
        }

        if (card.SecurityCode.Length != testCard.SecurityCodeLength || !card.SecurityCode.All(char.IsAsciiDigit))
        {
            return InstrumentCheck.Refuse("INVALID_SECURITY_CODE", $"The security code has {testCard.SecurityCodeLength} digits.");
        }

        var last4 = digits[^4..];
        return InstrumentCheck.Accept(new PaymentInstrument(PaymentMethod.Card, $"{testCard.Brand} ····{last4}", testCard.Brand, last4));
    }

    public Task<RailResponse> StartAsync(RailPaymentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var digits = request.Input is CardInput card ? CardNumbers.Normalize(card.Number) : null;
        if (digits is null || !SandboxTestInstruments.TryGetCard(digits, out var testCard))
        {
            return Task.FromResult(RailResponse.Declined(DeclineCodes.DoNotHonor));
        }

        var reference = $"card_{request.AttemptId:N}";
        var scenario = testCard.Scenario;
        switch (scenario)
        {
            case CardScenario.DoNotHonor:
                return Task.FromResult(RailResponse.Declined(DeclineCodes.DoNotHonor));
            case CardScenario.InsufficientFunds:
                return Task.FromResult(RailResponse.Declined(DeclineCodes.InsufficientFunds));
            case CardScenario.ExpiredCard:
                return Task.FromResult(RailResponse.Declined(DeclineCodes.ExpiredCard));
            case CardScenario.IncorrectSecurityCode:
                return Task.FromResult(RailResponse.Declined(DeclineCodes.IncorrectSecurityCode));
            case CardScenario.NetworkUnavailable:
                return Task.FromResult(RailResponse.Failed(DeclineCodes.RailUnavailable));
        }

        _transactions[reference] = new CardTransaction(scenario);
        return Task.FromResult(scenario is CardScenario.OtpThenApproved or CardScenario.OtpAlwaysRejected
            ? RailResponse.ChallengeRequired(reference, ChallengeKind.Otp)
            : RailResponse.Approved(reference));
    }

    public Task<RailResponse> CompleteAuthenticationAsync(string railReference, AuthenticationResponse response, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (!_transactions.TryGetValue(railReference, out var transaction) || response is not OtpResponse otp)
        {
            return Task.FromResult(RailResponse.Failed(DeclineCodes.RailUnavailable));
        }

        return Task.FromResult(transaction.Scenario == CardScenario.OtpThenApproved && string.Equals(otp.Code, SandboxTestInstruments.Otp, StringComparison.Ordinal)
            ? RailResponse.Approved(railReference)
            : RailResponse.Rejected());
    }

    public Task<DateTimeOffset> CaptureAsync(string railReference, Money amount, CancellationToken cancellationToken) =>
        Task.FromResult(Transaction(railReference).CapturedAt ??= time.GetUtcNow());

    public Task<DateTimeOffset> RefundAsync(string railReference, Money amount, string idempotencyKey, CancellationToken cancellationToken)
    {
        var transaction = Transaction(railReference);
        return Task.FromResult(transaction.Refunds.GetOrAdd(idempotencyKey, _ => time.GetUtcNow()));
    }

    private CardTransaction Transaction(string railReference) =>
        _transactions.TryGetValue(railReference, out var transaction)
            ? transaction
            : throw new InvalidOperationException("The sandbox card network does not know that payment (it forgets them when the API restarts).");

    private sealed class CardTransaction(CardScenario scenario)
    {
        public CardScenario Scenario { get; } = scenario;

        public DateTimeOffset? CapturedAt { get; set; }

        public ConcurrentDictionary<string, DateTimeOffset> Refunds { get; } = new(StringComparer.Ordinal);
    }
}

/// <summary>The sandbox's simulated UPI network: collect requests the customer approves or declines, kept in memory.</summary>
public sealed partial class SandboxUpiRail(TimeProvider time) : IPaymentRail
{
    private readonly ConcurrentDictionary<string, UpiTransaction> _transactions = new(StringComparer.Ordinal);

    public string Name => "sandbox-upi-network";

    public PaymentMethod Method => PaymentMethod.Upi;

    public bool IsSandbox => true;

    public InstrumentCheck Check(PaymentInstrumentInput input, DateTimeOffset now)
    {
        if (input is not UpiInput upi)
        {
            return InstrumentCheck.Refuse("INVALID_INSTRUMENT", "A UPI address is needed to pay by UPI.");
        }

        var address = upi.Address.Trim().ToLowerInvariant();
        if (!AddressPattern().IsMatch(address))
        {
            return InstrumentCheck.Refuse("INVALID_UPI_ADDRESS", "A UPI address looks like name@bank.");
        }

        if (!address.EndsWith(SandboxTestInstruments.UpiSuffix, StringComparison.Ordinal))
        {
            return InstrumentCheck.Refuse("TEST_UPI_ONLY", "This is a sandbox: only the listed test UPI addresses work. Your address was not accepted and nothing was kept.");
        }

        return InstrumentCheck.Accept(new PaymentInstrument(PaymentMethod.Upi, address));
    }

    public Task<RailResponse> StartAsync(RailPaymentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var local = request.Input is UpiInput upi ? upi.Address.Trim().ToLowerInvariant().Split('@')[0] : string.Empty;
        switch (local)
        {
            case "invalid":
                return Task.FromResult(RailResponse.Declined(DeclineCodes.InvalidUpiAddress));
            case "unavailable":
                return Task.FromResult(RailResponse.Failed(DeclineCodes.RailUnavailable));
        }

        var reference = $"upi_{request.AttemptId:N}";
        _transactions[reference] = new UpiTransaction(local);
        return Task.FromResult(RailResponse.ChallengeRequired(reference, ChallengeKind.UpiApproval));
    }

    public Task<RailResponse> CompleteAuthenticationAsync(string railReference, AuthenticationResponse response, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (!_transactions.TryGetValue(railReference, out var transaction) || response is not ApprovalResponse approval)
        {
            return Task.FromResult(RailResponse.Failed(DeclineCodes.RailUnavailable));
        }

        if (!approval.Approved)
        {
            return Task.FromResult(RailResponse.Declined(DeclineCodes.CustomerDeclined));
        }

        return Task.FromResult(transaction.Local switch
        {
            "insufficient" => RailResponse.Declined(DeclineCodes.InsufficientFunds),
            "timeout" => RailResponse.Declined(DeclineCodes.TimedOut),
            _ => RailResponse.Approved(railReference),
        });
    }

    // UPI takes the money when the customer approves, so capturing only confirms it.
    public Task<DateTimeOffset> CaptureAsync(string railReference, Money amount, CancellationToken cancellationToken) =>
        Task.FromResult(Transaction(railReference).CapturedAt ??= time.GetUtcNow());

    public Task<DateTimeOffset> RefundAsync(string railReference, Money amount, string idempotencyKey, CancellationToken cancellationToken) =>
        Task.FromResult(Transaction(railReference).Refunds.GetOrAdd(idempotencyKey, _ => time.GetUtcNow()));

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{1,63}@[a-z0-9]{2,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex AddressPattern();

    private UpiTransaction Transaction(string railReference) =>
        _transactions.TryGetValue(railReference, out var transaction)
            ? transaction
            : throw new InvalidOperationException("The sandbox UPI network does not know that payment (it forgets them when the API restarts).");

    private sealed class UpiTransaction(string local)
    {
        public string Local { get; } = local;

        public DateTimeOffset? CapturedAt { get; set; }

        public ConcurrentDictionary<string, DateTimeOffset> Refunds { get; } = new(StringComparer.Ordinal);
    }
}

/// <summary>The processor's records in memory: lost on restart, one instance only. Attempts are stored as copies.</summary>
public sealed class InMemoryPaymentAttemptStore : IPaymentAttemptStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, PaymentAttempt> _attempts = [];

    public Task AddAsync(PaymentAttempt attempt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        lock (_gate)
        {
            if (_attempts.ContainsKey(attempt.AttemptId))
            {
                throw new InvalidOperationException($"Payment attempt {attempt.AttemptId} already exists.");
            }

            RefuseReusedIdentifiers(attempt);
            if (attempt.WasCaptured && _attempts.Values.Any(other => other.OrderId == attempt.OrderId && other.WasCaptured))
            {
                throw new PaymentAttemptConcurrencyException("The order already has a captured payment.");
            }

            attempt.Version = 1;
            _attempts[attempt.AttemptId] = attempt.Copy();
        }

        return Task.CompletedTask;
    }

    public Task SaveAsync(PaymentAttempt attempt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        lock (_gate)
        {
            if (!_attempts.TryGetValue(attempt.AttemptId, out var stored) || stored.Version != attempt.Version)
            {
                throw new PaymentAttemptConcurrencyException();
            }

            RefuseReusedIdentifiers(attempt);
            if (attempt.WasCaptured && _attempts.Values.Any(other => other.OrderId == attempt.OrderId && other.AttemptId != attempt.AttemptId && other.WasCaptured))
            {
                throw new PaymentAttemptConcurrencyException("The order already has a captured payment.");
            }

            attempt.Version++;
            _attempts[attempt.AttemptId] = attempt.Copy();
        }

        return Task.CompletedTask;
    }

    public Task<PaymentAttempt?> FindAsync(Guid attemptId, CancellationToken cancellationToken) =>
        Task.FromResult(Read(() => _attempts.TryGetValue(attemptId, out var attempt) ? attempt.Copy() : null));

    public Task<IReadOnlyList<PaymentAttempt>> ListForOrderAsync(Guid orderId, CancellationToken cancellationToken) =>
        Task.FromResult(List(attempt => attempt.OrderId == orderId, attempt => attempt.CreatedAt, int.MaxValue));

    public Task<PaymentAttempt?> FindCapturedForOrderAsync(Guid orderId, CancellationToken cancellationToken) =>
        Task.FromResult(Read(() => _attempts.Values.FirstOrDefault(attempt => attempt.OrderId == orderId && attempt.WasCaptured)?.Copy()));

    public Task<IReadOnlyList<PaymentAttempt>> ListCapturedNotRecordedAsync(int limit, CancellationToken cancellationToken) =>
        Task.FromResult(List(attempt => attempt.WasCaptured && attempt.RecordedOnOrderAt is null, attempt => attempt.CapturedAt ?? attempt.CreatedAt, limit));

    public Task<IReadOnlyList<PaymentAttempt>> ListPendingExpiredAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken) =>
        Task.FromResult(List(attempt => attempt.IsExpiredAt(now), attempt => attempt.ExpiresAt, limit));

    public Task<IReadOnlyList<PaymentAttempt>> ListSettledBetweenAsync(DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken) =>
        Task.FromResult(List(
            attempt => (attempt.CapturedAt >= windowStart && attempt.CapturedAt < windowEnd) || (attempt.RefundedAt >= windowStart && attempt.RefundedAt < windowEnd),
            attempt => attempt.CapturedAt ?? attempt.CreatedAt,
            int.MaxValue));

    /// <summary>What PostgreSQL's unique constraints do: a payment or refund identifier belongs to one attempt only.</summary>
    private void RefuseReusedIdentifiers(PaymentAttempt attempt)
    {
        foreach (var other in _attempts.Values)
        {
            if (other.AttemptId != attempt.AttemptId
                && ((attempt.ProviderPaymentId is not null && other.ProviderPaymentId == attempt.ProviderPaymentId)
                    || (attempt.RefundId is not null && other.RefundId == attempt.RefundId)))
            {
                throw new InvalidOperationException("An attempt, payment or refund identifier was already used by another payment attempt.");
            }
        }
    }

    private T Read<T>(Func<T> read)
    {
        lock (_gate)
        {
            return read();
        }
    }

    private IReadOnlyList<PaymentAttempt> List(Func<PaymentAttempt, bool> filter, Func<PaymentAttempt, DateTimeOffset> order, int limit)
    {
        lock (_gate)
        {
            return [.. _attempts.Values.Where(filter).OrderBy(order).ThenBy(attempt => attempt.AttemptId).Take(limit).Select(attempt => attempt.Copy())];
        }
    }
}
