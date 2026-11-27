using FiatCryptoGateway.Domain.Assets;
using FiatCryptoGateway.Domain.Payments;

namespace FiatCryptoGateway.Tests;

public sealed class PaymentAttemptTests
{
    private static readonly DateTimeOffset Start = Fixtures.Start;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    [Fact]
    public void ANewAttempt_IsStarted_WithOneEventInItsHistory()
    {
        var attempt = NewAttempt();

        Assert.Equal(PaymentAttemptStatus.Started, attempt.Status);
        Assert.True(attempt.IsPending);
        Assert.Equal(Start + Window, attempt.ExpiresAt);
        var first = Assert.Single(attempt.Events);
        Assert.Equal((1, (PaymentAttemptStatus?)null, PaymentAttemptStatus.Started, "ATTEMPT_STARTED"), (first.Sequence, first.From, first.To, first.Reason));
    }

    [Fact]
    public void ASuccessfulPayment_GoesStartedAuthorizedCapturedRefunded_ThroughAnAppendOnlyHistory()
    {
        var attempt = NewAttempt();

        attempt.Authorize("rail_1", Start.AddSeconds(1));
        attempt.Capture("gp_pay_1", Start.AddSeconds(2));
        attempt.MarkRecordedOnOrder(Start.AddSeconds(3));
        attempt.Refund("gp_rfd_1", Start.AddHours(1));

        Assert.Equal(PaymentAttemptStatus.Refunded, attempt.Status);
        Assert.True(attempt.WasCaptured);
        Assert.Equal(
            [
                (1, null, PaymentAttemptStatus.Started), (2, PaymentAttemptStatus.Started, PaymentAttemptStatus.Authorized),
                (3, PaymentAttemptStatus.Authorized, PaymentAttemptStatus.Captured), (4, PaymentAttemptStatus.Captured, PaymentAttemptStatus.Refunded),
            ],
            attempt.Events.Select(entry => (entry.Sequence, entry.From, entry.To)));
        Assert.Equal(("rail_1", "gp_pay_1", Start.AddSeconds(2), Start.AddSeconds(3), "gp_rfd_1"), (attempt.RailReference, attempt.ProviderPaymentId, attempt.CapturedAt, attempt.RecordedOnOrderAt, attempt.RefundId));
    }

    [Fact]
    public void AuthenticationAnswers_AreLimited_ThenTheAttemptFails()
    {
        var attempt = NewAttempt();
        attempt.RequireAuthentication("rail_1", ChallengeKind.Otp, maxTries: 3, Start.AddSeconds(1));

        Assert.True(attempt.RejectAuthentication(Start.AddSeconds(2)));
        Assert.True(attempt.RejectAuthentication(Start.AddSeconds(3)));
        Assert.Equal(1, attempt.Challenge!.TriesLeft);
        Assert.False(attempt.RejectAuthentication(Start.AddSeconds(4)));

        Assert.Equal((PaymentAttemptStatus.AuthenticationFailed, DeclineCodes.AuthenticationFailed), (attempt.Status, attempt.DeclineCode));
        Assert.False(attempt.IsPending);
    }

    [Fact]
    public void ACorrectAnswerAfterWrongOnes_StillAuthorizes()
    {
        var attempt = NewAttempt();
        attempt.RequireAuthentication("rail_1", ChallengeKind.Otp, maxTries: 3, Start.AddSeconds(1));
        attempt.RejectAuthentication(Start.AddSeconds(2));

        attempt.Authorize("rail_1", Start.AddSeconds(3));

        Assert.Equal(PaymentAttemptStatus.Authorized, attempt.Status);
        Assert.Equal(1, attempt.Challenge!.TriesUsed);
    }

    [Theory]
    [InlineData(PaymentAttemptStatus.Started, PaymentAttemptStatus.Captured)]
    [InlineData(PaymentAttemptStatus.Started, PaymentAttemptStatus.Refunded)]
    [InlineData(PaymentAttemptStatus.Declined, PaymentAttemptStatus.Authorized)]
    [InlineData(PaymentAttemptStatus.Expired, PaymentAttemptStatus.Authorized)]
    [InlineData(PaymentAttemptStatus.Captured, PaymentAttemptStatus.Declined)]
    [InlineData(PaymentAttemptStatus.Authorized, PaymentAttemptStatus.Expired)]
    [InlineData(PaymentAttemptStatus.Refunded, PaymentAttemptStatus.Captured)]
    public void IllegalSteps_AreRefused(PaymentAttemptStatus from, PaymentAttemptStatus to)
    {
        var attempt = AttemptIn(from);

        Assert.Throws<InvalidOperationException>(() => Move(attempt, to));
    }

    [Fact]
    public void AuthenticationCanOnlyFail_AfterItWasRequired()
    {
        var attempt = NewAttempt();

        Assert.Throws<InvalidOperationException>(() => attempt.FailAuthentication(DeclineCodes.AuthenticationFailed, Start));
    }

    [Fact]
    public void AttemptsThatAreOver_CannotBeReopened()
    {
        foreach (var finished in new[] { PaymentAttemptStatus.Declined, PaymentAttemptStatus.Failed, PaymentAttemptStatus.Expired, PaymentAttemptStatus.AuthenticationFailed, PaymentAttemptStatus.Refunded })
        {
            var attempt = AttemptIn(finished);
            Assert.Throws<InvalidOperationException>(() => attempt.Authorize("rail_2", Start.AddHours(2)));
            Assert.Throws<InvalidOperationException>(() => attempt.Capture("gp_pay_2", Start.AddHours(2)));
        }
    }

    [Fact]
    public void AMoneyStepNeedsTheRightState_ToBeRecorded()
    {
        var attempt = NewAttempt();

        Assert.Throws<InvalidOperationException>(() => attempt.MarkRecordedOnOrder(Start));
        Assert.Throws<InvalidOperationException>(() => attempt.RejectAuthentication(Start));
    }

    [Theory]
    [InlineData("declined")]
    [InlineData("Card 4111 1111 1111 1111")]
    [InlineData("")]
    public void Reasons_AreStableCodes_NeverFreeText(string code)
    {
        var attempt = NewAttempt();

        Assert.ThrowsAny<ArgumentException>(() => attempt.Decline(code, Start));
    }

    [Fact]
    public void OnlyPendingAttempts_Expire()
    {
        var attempt = NewAttempt();
        Assert.False(attempt.IsExpiredAt(Start + Window - TimeSpan.FromSeconds(1)));
        Assert.True(attempt.IsExpiredAt(Start + Window));

        attempt.Authorize("rail_1", Start.AddSeconds(1));
        Assert.False(attempt.IsExpiredAt(Start + Window + TimeSpan.FromHours(1)));
    }

    [Fact]
    public void ACopy_IsIndependentOfTheOriginal()
    {
        var attempt = NewAttempt();
        attempt.RequireAuthentication("rail_1", ChallengeKind.UpiApproval, 1, Start.AddSeconds(1));
        var copy = attempt.Copy();

        attempt.Authorize("rail_1", Start.AddSeconds(2));

        Assert.Equal(PaymentAttemptStatus.AwaitingAuthentication, copy.Status);
        Assert.Equal(2, copy.Events.Count);
        Assert.Equal(3, attempt.Events.Count);
        Assert.Equal(ChallengeKind.UpiApproval, copy.Challenge!.Kind);
    }

    [Theory]
    [InlineData("Visa ····1111", true)]
    [InlineData("success@sandbox", true)]
    [InlineData("Visa 4111 1111 1111 1111", false)]
    [InlineData("4111-1111-1111-1111", false)]
    [InlineData("card 4111111111111", false)]
    [InlineData("order 123456789012", true)]
    public void AnInstrumentLabel_NeverHoldsACardNumber(string label, bool allowed)
    {
        if (allowed)
        {
            Assert.Equal(label, new PaymentInstrument(PaymentMethod.Card, label).Label);
        }
        else
        {
            Assert.Throws<ArgumentException>(() => new PaymentInstrument(PaymentMethod.Card, label));
        }
    }

    [Theory]
    [InlineData("1111", true)]
    [InlineData("111", false)]
    [InlineData("11111", false)]
    [InlineData("11a1", false)]
    public void TheLastDigits_AreExactlyFour(string last4, bool allowed)
    {
        if (allowed)
        {
            Assert.Equal(last4, new PaymentInstrument(PaymentMethod.Card, "Visa ····1111", "Visa", last4).Last4);
        }
        else
        {
            Assert.Throws<ArgumentException>(() => new PaymentInstrument(PaymentMethod.Card, "Visa", "Visa", last4));
        }
    }

    [Theory]
    [InlineData("4111111111111111", "4111111111111111")]
    [InlineData("4111 1111 1111 1111", "4111111111111111")]
    [InlineData("4111-1111-1111-1111", "4111111111111111")]
    [InlineData(" 3782 822463 10005 ", "378282246310005")]
    [InlineData("4111 1111 1111 111a", null)]
    [InlineData("4111", null)]
    [InlineData("41111111111111111111", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void CardNumbers_AreNormalisedOrRefused(string? typed, string? digits)
    {
        Assert.Equal(digits, CardNumbers.Normalize(typed));
    }

    [Theory]
    [InlineData("4111111111111111", true)]
    [InlineData("4242424242424242", true)]
    [InlineData("378282246310005", true)]
    [InlineData("4111111111111112", false)]
    [InlineData("4242424242424241", false)]
    [InlineData("", false)]
    public void TheCardChecksum_CatchesTypingSlips(string digits, bool valid)
    {
        Assert.Equal(valid, CardNumbers.PassesLuhn(digits));
    }

    private static PaymentAttempt NewAttempt() =>
        PaymentAttempt.Start(
            Guid.NewGuid(), Guid.NewGuid(), Fixtures.ClientId, new Money(10_000m, Fixtures.Inr),
            new PaymentInstrument(PaymentMethod.Card, "Visa ····1111", "Visa", "1111"), Start, Window);

    /// <summary>An attempt brought to <paramref name="status"/> by the legal steps.</summary>
    private static PaymentAttempt AttemptIn(PaymentAttemptStatus status)
    {
        var attempt = NewAttempt();
        var at = Start.AddSeconds(1);
        switch (status)
        {
            case PaymentAttemptStatus.Started:
                break;
            case PaymentAttemptStatus.AwaitingAuthentication:
                attempt.RequireAuthentication("rail_1", ChallengeKind.Otp, 3, at);
                break;
            case PaymentAttemptStatus.Authorized:
                attempt.Authorize("rail_1", at);
                break;
            case PaymentAttemptStatus.Captured:
                attempt.Authorize("rail_1", at);
                attempt.Capture("gp_pay_1", at);
                break;
            case PaymentAttemptStatus.Refunded:
                attempt.Authorize("rail_1", at);
                attempt.Capture("gp_pay_1", at);
                attempt.Refund("gp_rfd_1", at);
                break;
            case PaymentAttemptStatus.Declined:
                attempt.Decline(DeclineCodes.DoNotHonor, at);
                break;
            case PaymentAttemptStatus.Failed:
                attempt.Fail(DeclineCodes.RailUnavailable, at);
                break;
            case PaymentAttemptStatus.Expired:
                attempt.Expire(DeclineCodes.TimedOut, at);
                break;
            case PaymentAttemptStatus.AuthenticationFailed:
                attempt.RequireAuthentication("rail_1", ChallengeKind.Otp, 3, at);
                attempt.FailAuthentication(DeclineCodes.AuthenticationFailed, at);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(status));
        }

        return attempt;
    }

    private static void Move(PaymentAttempt attempt, PaymentAttemptStatus to)
    {
        var at = Start.AddHours(2);
        switch (to)
        {
            case PaymentAttemptStatus.Authorized:
                attempt.Authorize("rail_9", at);
                break;
            case PaymentAttemptStatus.Captured:
                attempt.Capture("gp_pay_9", at);
                break;
            case PaymentAttemptStatus.Refunded:
                attempt.Refund("gp_rfd_9", at);
                break;
            case PaymentAttemptStatus.Declined:
                attempt.Decline(DeclineCodes.DoNotHonor, at);
                break;
            case PaymentAttemptStatus.Expired:
                attempt.Expire(DeclineCodes.TimedOut, at);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(to));
        }
    }
}
