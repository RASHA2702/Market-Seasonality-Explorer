using FiatCryptoGateway.Application.Payments;
using FiatCryptoGateway.Domain.Assets;
using FiatCryptoGateway.Domain.Payments;
using FiatCryptoGateway.Sandbox;

namespace FiatCryptoGateway.Tests;

public sealed class SandboxPaymentRailTests
{
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly DateTimeOffset Now = Fixtures.Start; // 24 November 2026

    private readonly ManualTimeProvider _time = new(Fixtures.Start);

    public static TheoryData<string> TestCardNumbers => [.. SandboxTestInstruments.Cards.Select(card => card.Number)];

    [Fact]
    public void TheCatalogue_HoldsUniqueChecksumValidNumbers_AndEveryOneIsAccepted()
    {
        var rail = Card();

        Assert.Equal(SandboxTestInstruments.Cards.Count, SandboxTestInstruments.Cards.Select(card => card.Number).Distinct().Count());
        foreach (var card in SandboxTestInstruments.Cards)
        {
            Assert.True(CardNumbers.PassesLuhn(card.Number), card.Number);
            var check = rail.Check(Typed(card.Display, new string('1', card.SecurityCodeLength)), Now);

            Assert.True(check.Accepted, card.Number);
            var instrument = check.Instrument!;
            Assert.Equal((PaymentMethod.Card, card.Brand, card.Number[^4..], $"{card.Brand} ····{card.Number[^4..]}"), (instrument.Method, instrument.Brand, instrument.Last4, instrument.Label));
            Assert.DoesNotContain(card.Number, instrument.Label, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("4012888888881881", "TEST_CARDS_ONLY")]   // a real-looking number that passes the checksum: not a listed test card
    [InlineData("5200 8282 8282 8210", "TEST_CARDS_ONLY")]
    [InlineData("4111 1111 1111 1112", "INVALID_CARD_NUMBER")] // fails the checksum
    [InlineData("4111 1111 1111 111x", "INVALID_CARD_NUMBER")]
    [InlineData("4111", "INVALID_CARD_NUMBER")]
    [InlineData("", "INVALID_CARD_NUMBER")]
    public void ANumberThatIsNotATestCard_IsRefused_AndNeverEchoed(string typed, string code)
    {
        var check = Card().Check(Typed(typed, "123"), Now);

        Assert.False(check.Accepted);
        Assert.Equal(code, check.RefusalCode);
        var compact = typed.Replace(" ", string.Empty, StringComparison.Ordinal);
        if (compact.Length > 0)
        {
            Assert.DoesNotContain(compact, check.RefusalMessage!.Replace(" ", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheNumberIsCheckedFirst_SoAnUnlistedCardIsRefusedWhateverElseIsWrong()
    {
        var check = Card().Check(new CardInput("4012888888881881", 13, 1999, "x", "T"), Now);

        Assert.Equal("TEST_CARDS_ONLY", check.RefusalCode);
    }

    [Theory]
    [InlineData(12, 2030, "123", null)]
    [InlineData(11, 2026, "123", null)]                   // this month is still valid
    [InlineData(10, 2026, "123", "INVALID_EXPIRY")]       // last month
    [InlineData(13, 2030, "123", "INVALID_EXPIRY")]
    [InlineData(0, 2030, "123", "INVALID_EXPIRY")]
    [InlineData(1, 2047, "123", "INVALID_EXPIRY")]        // more than twenty years away
    [InlineData(12, 2030, "12", "INVALID_SECURITY_CODE")]
    [InlineData(12, 2030, "1234", "INVALID_SECURITY_CODE")]
    [InlineData(12, 2030, "12a", "INVALID_SECURITY_CODE")]
    public void ExpiryAndSecurityCode_AreChecked_ForAListedCard(int month, int year, string securityCode, string? refusal)
    {
        var check = Card().Check(new CardInput("4111111111111111", month, year, securityCode, "T"), Now);

        Assert.Equal(refusal, check.RefusalCode);
        Assert.Equal(refusal is null, check.Accepted);
    }

    [Fact]
    public void AmericanExpress_NeedsAFourDigitSecurityCode()
    {
        var rail = Card();

        Assert.Equal("INVALID_SECURITY_CODE", rail.Check(Typed("378282246310005", "123"), Now).RefusalCode);
        Assert.True(rail.Check(Typed("378282246310005", "1234"), Now).Accepted);
    }

    [Fact]
    public void TheCardRail_RefusesAUpiInput()
    {
        Assert.Equal("INVALID_INSTRUMENT", Card().Check(new UpiInput("success@sandbox"), Now).RefusalCode);
        Assert.Equal("INVALID_INSTRUMENT", Upi().Check(Typed("4111111111111111", "123"), Now).RefusalCode);
    }

    [Theory]
    [InlineData("4111111111111111", RailOutcome.Approved, null)]
    [InlineData("378282246310005", RailOutcome.Approved, null)]
    [InlineData("4242424242424242", RailOutcome.ChallengeRequired, null)]
    [InlineData("5555555555554444", RailOutcome.ChallengeRequired, null)]
    [InlineData("4000000000003220", RailOutcome.ChallengeRequired, null)]
    [InlineData("4000000000000002", RailOutcome.Declined, DeclineCodes.DoNotHonor)]
    [InlineData("4000000000009995", RailOutcome.Declined, DeclineCodes.InsufficientFunds)]
    [InlineData("4000000000000069", RailOutcome.Declined, DeclineCodes.ExpiredCard)]
    [InlineData("4000000000000127", RailOutcome.Declined, DeclineCodes.IncorrectSecurityCode)]
    [InlineData("4000000000000259", RailOutcome.Failed, DeclineCodes.RailUnavailable)]
    public async Task EachTestCard_BehavesAsItIsListed(string number, RailOutcome outcome, string? code)
    {
        var attemptId = Guid.NewGuid();

        var response = await Card().StartAsync(Request(attemptId, Typed(number, number.StartsWith('3') ? "1234" : "123")), None);

        Assert.Equal((outcome, code), (response.Outcome, response.Code));
        if (outcome is RailOutcome.Approved or RailOutcome.ChallengeRequired)
        {
            Assert.Equal($"card_{attemptId:N}", response.RailReference);
        }

        if (outcome == RailOutcome.ChallengeRequired)
        {
            Assert.Equal(ChallengeKind.Otp, response.Challenge);
        }
    }

    [Fact]
    public async Task TheOneTimePassword_IsAcceptedOnlyForACardThatAllowsIt()
    {
        var rail = Card();
        var accepted = (await rail.StartAsync(Request(Guid.NewGuid(), Typed("4242424242424242", "123")), None)).RailReference!;
        var wrongThenRight = (await rail.StartAsync(Request(Guid.NewGuid(), Typed("5555555555554444", "123")), None)).RailReference!;
        var neverAccepted = (await rail.StartAsync(Request(Guid.NewGuid(), Typed("4000000000003220", "123")), None)).RailReference!;

        Assert.Equal(RailOutcome.Approved, (await rail.CompleteAuthenticationAsync(accepted, new OtpResponse(SandboxTestInstruments.Otp), None)).Outcome);
        Assert.Equal(RailOutcome.AuthenticationRejected, (await rail.CompleteAuthenticationAsync(wrongThenRight, new OtpResponse("000000"), None)).Outcome);
        Assert.Equal(RailOutcome.Approved, (await rail.CompleteAuthenticationAsync(wrongThenRight, new OtpResponse(SandboxTestInstruments.Otp), None)).Outcome);
        Assert.Equal(RailOutcome.AuthenticationRejected, (await rail.CompleteAuthenticationAsync(neverAccepted, new OtpResponse(SandboxTestInstruments.Otp), None)).Outcome);
    }

    [Fact]
    public async Task AnUnknownPayment_OrTheWrongKindOfAnswer_IsANetworkFailure()
    {
        var rail = Card();
        var reference = (await rail.StartAsync(Request(Guid.NewGuid(), Typed("4242424242424242", "123")), None)).RailReference!;

        Assert.Equal(RailOutcome.Failed, (await rail.CompleteAuthenticationAsync("card_unknown", new OtpResponse(SandboxTestInstruments.Otp), None)).Outcome);
        Assert.Equal(RailOutcome.Failed, (await rail.CompleteAuthenticationAsync(reference, new ApprovalResponse(true), None)).Outcome);
    }

    [Fact]
    public async Task CapturingAndRefunding_AreIdempotent()
    {
        var rail = Card();
        var reference = (await rail.StartAsync(Request(Guid.NewGuid(), Typed("4111111111111111", "123")), None)).RailReference!;
        var amount = new Money(10_000m, Fixtures.Inr);

        var captured = await rail.CaptureAsync(reference, amount, None);
        _time.Advance(TimeSpan.FromMinutes(5));
        var capturedAgain = await rail.CaptureAsync(reference, amount, None);
        var refunded = await rail.RefundAsync(reference, amount, "refund-1", None);
        _time.Advance(TimeSpan.FromMinutes(5));
        var refundedAgain = await rail.RefundAsync(reference, amount, "refund-1", None);
        var another = await rail.RefundAsync(reference, amount, "refund-2", None);

        Assert.Equal(Fixtures.Start, captured);
        Assert.Equal(captured, capturedAgain);
        Assert.Equal(refunded, refundedAgain);
        Assert.True(another > refunded);
        await Assert.ThrowsAsync<InvalidOperationException>(() => rail.CaptureAsync("card_unknown", amount, None));
    }

    [Theory]
    [InlineData("success@sandbox")]
    [InlineData("SUCCESS@Sandbox")]
    [InlineData("insufficient@sandbox")]
    [InlineData("timeout@sandbox")]
    [InlineData("invalid@sandbox")]
    [InlineData("unavailable@sandbox")]
    public void TestUpiAddresses_AreAccepted_InAnyCase(string address)
    {
        var check = Upi().Check(new UpiInput(address), Now);

        Assert.True(check.Accepted);
        Assert.Equal(address.ToLowerInvariant(), check.Instrument!.Label);
        Assert.Equal(PaymentMethod.Upi, check.Instrument.Method);
    }

    [Theory]
    [InlineData("name@oksbi", "TEST_UPI_ONLY")]
    [InlineData("9876543210@ybl", "TEST_UPI_ONLY")]
    [InlineData("not-an-address", "INVALID_UPI_ADDRESS")]
    [InlineData("a@sandbox", "INVALID_UPI_ADDRESS")]
    [InlineData("", "INVALID_UPI_ADDRESS")]
    public void AnythingElse_IsRefused_AndNeverEchoed(string address, string code)
    {
        var check = Upi().Check(new UpiInput(address), Now);

        Assert.False(check.Accepted);
        Assert.Equal(code, check.RefusalCode);
        if (address.Length > 0)
        {
            Assert.DoesNotContain(address, check.RefusalMessage!, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("success@sandbox", true, RailOutcome.Approved, null)]
    [InlineData("success@sandbox", false, RailOutcome.Declined, DeclineCodes.CustomerDeclined)]
    [InlineData("insufficient@sandbox", true, RailOutcome.Declined, DeclineCodes.InsufficientFunds)]
    [InlineData("timeout@sandbox", true, RailOutcome.Declined, DeclineCodes.TimedOut)]
    public async Task AUpiRequest_IsApprovedOrDeclinedByTheCustomer_ThenTheBankDecides(string address, bool approves, RailOutcome outcome, string? code)
    {
        var rail = Upi();
        var started = await rail.StartAsync(Request(Guid.NewGuid(), new UpiInput(address)), None);

        Assert.Equal((RailOutcome.ChallengeRequired, ChallengeKind.UpiApproval), (started.Outcome, started.Challenge!.Value));
        var finished = await rail.CompleteAuthenticationAsync(started.RailReference!, new ApprovalResponse(approves), None);

        Assert.Equal((outcome, code), (finished.Outcome, finished.Code));
    }

    [Theory]
    [InlineData("invalid@sandbox", RailOutcome.Declined, DeclineCodes.InvalidUpiAddress)]
    [InlineData("unavailable@sandbox", RailOutcome.Failed, DeclineCodes.RailUnavailable)]
    public async Task SomeUpiAddresses_AreAnsweredAtOnce(string address, RailOutcome outcome, string code)
    {
        var response = await Upi().StartAsync(Request(Guid.NewGuid(), new UpiInput(address)), None);

        Assert.Equal((outcome, code), (response.Outcome, response.Code));
    }

    [Fact]
    public void WhatTheCustomerTyped_NeverPrints()
    {
        var card = new CardInput("4111 1111 1111 1111", 12, 2030, "737", "Test Customer");
        var request = Request(Guid.NewGuid(), card);
        var printed = new[]
        {
            card.ToString(), new UpiInput("success@sandbox").ToString(), new OtpResponse("123456").ToString(),
            new ApprovalResponse(true).ToString(), request.ToString(), request.Input.ToString(),
        };

        foreach (var text in printed)
        {
            Assert.DoesNotContain("4111", text, StringComparison.Ordinal);
            Assert.DoesNotContain("737", text, StringComparison.Ordinal);
            Assert.DoesNotContain("123456", text, StringComparison.Ordinal);
            Assert.DoesNotContain("success@", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Test Customer", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EveryRail_IsASandboxRail()
    {
        IPaymentRail[] rails = [Card(), Upi()];

        Assert.All(rails, rail => Assert.True(rail.IsSandbox));
        Assert.Equal([PaymentMethod.Card, PaymentMethod.Upi], rails.Select(rail => rail.Method));
    }

    private SandboxCardRail Card() => new(_time);

    private SandboxUpiRail Upi() => new(_time);

    private static CardInput Typed(string number, string securityCode) => new(number, 12, 2030, securityCode, "Test Customer");

    private static RailPaymentRequest Request(Guid attemptId, PaymentInstrumentInput input) =>
        new(attemptId, Guid.NewGuid(), new Money(10_000m, Fixtures.Inr), input);
}
