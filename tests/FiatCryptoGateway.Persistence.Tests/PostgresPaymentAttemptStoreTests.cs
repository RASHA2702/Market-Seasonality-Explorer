using System.Security.Cryptography;
using System.Text;
using FiatCryptoGateway.Application.OnRamp;
using FiatCryptoGateway.Application.Payments;
using FiatCryptoGateway.Application.Settlement;
using FiatCryptoGateway.Domain.Orders;
using FiatCryptoGateway.Domain.Payments;
using FiatCryptoGateway.Domain.Settlement;
using FiatCryptoGateway.Sandbox;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FiatCryptoGateway.Persistence.Tests;

[Collection(SharedPostgres.Name)]
public sealed class PostgresPaymentAttemptStoreTests(PostgresFixture fixture)
{
    private const string Address = "0x1111111111111111111111111111111111111111";
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly DateTimeOffset Start = new(2028, 5, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    [Fact]
    public async Task AnAttempt_IsStoredWithEverythingAboutIt_AndComesBackIdentical()
    {
        await using var services = fixture.BuildServices(TimeProvider.System);
        var store = services.GetRequiredService<IPaymentAttemptStore>();
        var order = await OrderAsync(services);
        var paymentId = Unique("gp_pay");
        var attempt = NewAttempt(order, Start, PaymentMethod.Card);
        await store.AddAsync(attempt, None);
        attempt.RequireAuthentication("card_1", ChallengeKind.Otp, 3, Start.AddSeconds(1));
        attempt.RejectAuthentication(Start.AddSeconds(2));
        await store.SaveAsync(attempt, None);
        attempt.Authorize("card_1", Start.AddSeconds(3));
        attempt.Capture(paymentId, Start.AddSeconds(4));
        attempt.MarkRecordedOnOrder(Start.AddSeconds(5));
        await store.SaveAsync(attempt, None);

        var found = (await store.FindAsync(attempt.AttemptId, None))!;

        Assert.Equal(3, found.Version);
        Assert.Equal((order.OrderId, order.ClientId, PaymentMethod.Card, order.Quote.SourceAmount), (found.OrderId, found.ClientId, found.Method, found.Amount));
        Assert.Equal(("Visa ····1111", "Visa", "1111"), (found.Instrument.Label, found.Instrument.Brand, found.Instrument.Last4));
        Assert.Equal((PaymentAttemptStatus.Captured, "card_1", paymentId), (found.Status, found.RailReference, found.ProviderPaymentId));
        Assert.Equal(new AuthenticationChallenge(ChallengeKind.Otp, 3, 1), found.Challenge);
        Assert.Equal((Start.AddSeconds(4), Start.AddSeconds(5)), (found.CapturedAt, found.RecordedOnOrderAt));
        Assert.Equal(attempt.Events, found.Events);
        Assert.Equal(
            [PaymentAttemptStatus.Started, PaymentAttemptStatus.AwaitingAuthentication, PaymentAttemptStatus.Authorized, PaymentAttemptStatus.Captured],
            found.Events.Select(entry => entry.To));
    }

    [Fact]
    public async Task AFailedAttempt_KeepsItsReason()
    {
        await using var services = fixture.BuildServices(TimeProvider.System);
        var store = services.GetRequiredService<IPaymentAttemptStore>();
        var attempt = NewAttempt(await OrderAsync(services), Start);
        await store.AddAsync(attempt, None);
        attempt.Decline(DeclineCodes.InsufficientFunds, Start.AddSeconds(1));
        await store.SaveAsync(attempt, None);

        var found = (await store.FindAsync(attempt.AttemptId, None))!;

        Assert.Equal((PaymentAttemptStatus.Declined, DeclineCodes.InsufficientFunds), (found.Status, found.DeclineCode));
        Assert.Null(await store.FindAsync(Guid.NewGuid(), None));
    }

    [Fact]
    public async Task AStaleCopy_CannotOverwriteANewerOne()
    {
        await using var services = fixture.BuildServices(TimeProvider.System);
        var store = services.GetRequiredService<IPaymentAttemptStore>();
        var attempt = NewAttempt(await OrderAsync(services), Start);
        await store.AddAsync(attempt, None);
        var first = (await store.FindAsync(attempt.AttemptId, None))!;
        var second = (await store.FindAsync(attempt.AttemptId, None))!;
        first.Authorize("rail_1", Start.AddSeconds(1));
        await store.SaveAsync(first, None);
        second.Decline(DeclineCodes.DoNotHonor, Start.AddSeconds(2));

        await Assert.ThrowsAsync<PaymentAttemptConcurrencyException>(() => store.SaveAsync(second, None));

        Assert.Equal(PaymentAttemptStatus.Authorized, (await store.FindAsync(attempt.AttemptId, None))!.Status);
    }

    [Fact]
    public async Task AnOrder_CanHaveSeveralAttempts_ButOnlyOneCaptured()
    {
        await using var services = fixture.BuildServices(TimeProvider.System);
        var store = services.GetRequiredService<IPaymentAttemptStore>();
        var order = await OrderAsync(services);
        var declined = NewAttempt(order, Start);
        var paid = NewAttempt(order, Start.AddMinutes(1));
        var second = NewAttempt(order, Start.AddMinutes(2));
        await store.AddAsync(declined, None);
        await store.AddAsync(paid, None);
        await store.AddAsync(second, None);
        declined.Decline(DeclineCodes.DoNotHonor, Start.AddSeconds(30));
        await store.SaveAsync(declined, None);
        paid.Authorize("rail_1", Start.AddMinutes(1));
        paid.Capture(Unique("gp_pay"), Start.AddMinutes(1));
        await store.SaveAsync(paid, None);
        second.Authorize("rail_2", Start.AddMinutes(2));
        second.Capture(Unique("gp_pay"), Start.AddMinutes(2));

        await Assert.ThrowsAsync<PaymentAttemptConcurrencyException>(() => store.SaveAsync(second, None));

        Assert.Equal([declined.AttemptId, paid.AttemptId, second.AttemptId], (await store.ListForOrderAsync(order.OrderId, None)).Select(attempt => attempt.AttemptId));
        Assert.Equal(paid.AttemptId, (await store.FindCapturedForOrderAsync(order.OrderId, None))!.AttemptId);
    }

    [Fact]
    public async Task TwoInstancesCapturingForTheSameOrder_AtOnce_OnlyOneWins()
    {
        await using var instanceA = fixture.BuildServices(TimeProvider.System);
        await using var instanceB = fixture.BuildServices(TimeProvider.System);
        var order = await OrderAsync(instanceA);
        var storeA = instanceA.GetRequiredService<IPaymentAttemptStore>();
        var storeB = instanceB.GetRequiredService<IPaymentAttemptStore>();
        var one = NewAttempt(order, Start);
        var other = NewAttempt(order, Start.AddSeconds(1));
        await storeA.AddAsync(one, None);
        await storeA.AddAsync(other, None);
        one.Authorize("rail_1", Start.AddSeconds(2));
        other.Authorize("rail_2", Start.AddSeconds(2));
        await storeA.SaveAsync(one, None);
        await storeA.SaveAsync(other, None);
        one.Capture(Unique("gp_pay"), Start.AddSeconds(3));
        other.Capture(Unique("gp_pay"), Start.AddSeconds(3));

        var outcomes = await Task.WhenAll(Try(storeA.SaveAsync(one, None)), Try(storeB.SaveAsync(other, None)));

        Assert.Single(outcomes, saved => saved);
        Assert.Equal(1, (await storeA.ListForOrderAsync(order.OrderId, None)).Count(attempt => attempt.WasCaptured));
    }

    [Fact]
    public async Task ARefundedAttempt_IsStillTheOrdersCapturedPayment_AndEndsFinal()
    {
        await using var services = fixture.BuildServices(TimeProvider.System);
        var store = services.GetRequiredService<IPaymentAttemptStore>();
        var order = await OrderAsync(services);
        var attempt = NewAttempt(order, Start);
        await store.AddAsync(attempt, None);
        attempt.Authorize("rail_1", Start.AddSeconds(1));
        attempt.Capture(Unique("gp_pay"), Start.AddSeconds(1));
        attempt.MarkRecordedOnOrder(Start.AddSeconds(2));
        await store.SaveAsync(attempt, None);
        var refundId = Unique("gp_rfd");
        attempt.Refund(refundId, Start.AddHours(1));
        await store.SaveAsync(attempt, None);

        var found = (await store.FindCapturedForOrderAsync(order.OrderId, None))!;

        Assert.Equal((PaymentAttemptStatus.Refunded, refundId, Start.AddHours(1)), (found.Status, found.RefundId, found.RefundedAt));
        Assert.Null(await store.FindCapturedForOrderAsync(Guid.NewGuid(), None));
    }

    [Fact]
    public async Task AnAttempt_IsAddedOnce()
    {
        await using var services = fixture.BuildServices(TimeProvider.System);
        var store = services.GetRequiredService<IPaymentAttemptStore>();
        var order = await OrderAsync(services);
        var attempt = NewAttempt(order, Start);
        await store.AddAsync(attempt, None);
        var again = PaymentAttempt.Start(attempt.AttemptId, order.OrderId, order.ClientId, order.Quote.SourceAmount, attempt.Instrument, Start, Window);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => store.AddAsync(again, None));

        Assert.IsType<PostgresException>(failure.InnerException);
        Assert.Single(await store.ListForOrderAsync(order.OrderId, None));
    }

    [Fact]
    public async Task APaymentIdentifier_BelongsToOneAttemptOnly_AndReuseIsABugNotALostRace()
    {
        await using var services = fixture.BuildServices(TimeProvider.System);
        var store = services.GetRequiredService<IPaymentAttemptStore>();
        var paymentId = Unique("gp_pay");
        var first = NewAttempt(await OrderAsync(services), Start);
        var second = NewAttempt(await OrderAsync(services), Start);
        await store.AddAsync(first, None);
        await store.AddAsync(second, None);
        first.Authorize("rail_1", Start.AddSeconds(1));
        first.Capture(paymentId, Start.AddSeconds(1));
        await store.SaveAsync(first, None);
        second.Authorize("rail_2", Start.AddSeconds(1));
        second.Capture(paymentId, Start.AddSeconds(1));

        // Not a PaymentAttemptConcurrencyException: callers handle that one as a lost race, and this is not one.
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(second, None));

        Assert.IsType<PostgresException>(failure.InnerException);
        Assert.Equal(PaymentAttemptStatus.Started, (await store.FindAsync(second.AttemptId, None))!.Status);
        Assert.Null(await store.FindCapturedForOrderAsync(second.OrderId, None));
    }

    [Fact]
    public async Task ARefundIdentifier_BelongsToOneAttemptOnly_AndReuseIsABugNotALostRace()
    {
        await using var services = fixture.BuildServices(TimeProvider.System);
        var store = services.GetRequiredService<IPaymentAttemptStore>();
        var refundId = Unique("gp_rfd");
        var first = await CapturedAsync(store, await OrderAsync(services));
        var second = await CapturedAsync(store, await OrderAsync(services));
        first.Refund(refundId, Start.AddHours(1));
        await store.SaveAsync(first, None);
        second.Refund(refundId, Start.AddHours(1));

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(second, None));

        Assert.IsType<PostgresException>(failure.InnerException);
        Assert.Equal(PaymentAttemptStatus.Captured, (await store.FindAsync(second.AttemptId, None))!.Status);
    }

    [Fact]
    public async Task TheQueries_FindWhatNeedsDoing_OldestFirst()
    {
        await using var services = fixture.BuildServices(TimeProvider.System);
        var store = services.GetRequiredService<IPaymentAttemptStore>();
        var order = await OrderAsync(services);
        var other = await OrderAsync(services);
        var yetOther = await OrderAsync(services);

        // Times far from any other test's, so these lists hold only this test's attempts.
        var at = new DateTimeOffset(2031, 1, 1, 9, 0, 0, TimeSpan.Zero);
        var unrecorded = NewAttempt(order, at);
        var recorded = NewAttempt(other, at.AddMinutes(1));
        var waiting = NewAttempt(yetOther, at.AddMinutes(2));
        foreach (var attempt in new[] { unrecorded, recorded, waiting })
        {
            await store.AddAsync(attempt, None);
        }

        foreach (var (attempt, when) in new[] { (unrecorded, at), (recorded, at.AddMinutes(1)) })
        {
            attempt.Authorize("rail", when.AddSeconds(1));
            attempt.Capture($"gp_pay_{attempt.AttemptId:N}", when.AddSeconds(1));
            await store.SaveAsync(attempt, None);
        }

        recorded.MarkRecordedOnOrder(at.AddMinutes(3));
        await store.SaveAsync(recorded, None);

        var notRecorded = await store.ListCapturedNotRecordedAsync(500, None);
        Assert.Contains(unrecorded.AttemptId, notRecorded.Select(attempt => attempt.AttemptId));
        Assert.DoesNotContain(recorded.AttemptId, notRecorded.Select(attempt => attempt.AttemptId));

        var expired = await store.ListPendingExpiredAsync(at.AddMinutes(30), 500, None);
        var expiredIds = expired.Select(attempt => attempt.AttemptId).ToList();
        Assert.Contains(waiting.AttemptId, expiredIds);
        Assert.DoesNotContain(unrecorded.AttemptId, expiredIds);
        Assert.DoesNotContain(waiting.AttemptId, (await store.ListPendingExpiredAsync(at.AddMinutes(5), 500, None)).Select(attempt => attempt.AttemptId));

        // Due exactly at its time limit, and not a moment before.
        Assert.Contains(waiting.AttemptId, (await store.ListPendingExpiredAsync(waiting.ExpiresAt, 500, None)).Select(attempt => attempt.AttemptId));
        Assert.DoesNotContain(waiting.AttemptId, (await store.ListPendingExpiredAsync(waiting.ExpiresAt.AddMicroseconds(-1), 500, None)).Select(attempt => attempt.AttemptId));

        var settled = (await store.ListSettledBetweenAsync(at, at.AddHours(1), None)).Select(attempt => attempt.AttemptId).ToList();
        Assert.Equal([unrecorded.AttemptId, recorded.AttemptId], settled);
        Assert.Empty(await store.ListSettledBetweenAsync(at.AddHours(2), at.AddHours(3), None));

        // A window includes its start and excludes its end, so a payment is in exactly one day's report.
        Assert.Equal(
            [unrecorded.AttemptId],
            (await store.ListSettledBetweenAsync(at.AddSeconds(1), at.AddMinutes(1).AddSeconds(1), None)).Select(attempt => attempt.AttemptId));
    }

    [Fact]
    public async Task TheDatabase_RefusesAnythingThatLooksLikeACardNumber_EvenIfTheCodeSlips()
    {
        await using var services = fixture.BuildServices(TimeProvider.System);
        var order = await OrderAsync(services);
        await using var connection = await services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO gateway.payment_attempt (
                attempt_id, order_id, client_id, method, amount_asset_key, amount, status, instrument_label, created_at, updated_at, expires_at, row_version)
            VALUES (@id, @order, @client, 'CARD', 'INR', 10000, 'STARTED', 'Visa 4111 1111 1111 1111', now(), now(), now() + interval '10 minutes', 1)
            """,
            connection);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("order", order.OrderId);
        command.Parameters.AddWithValue("client", order.ClientId);

        var failure = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());

        Assert.Equal(PostgresErrorCodes.CheckViolation, failure.SqlState);
    }

    /// <summary>Identifiers are unique in the database for good, and tests share one, so each test makes its own.</summary>
    private static string Unique(string prefix) => $"{prefix}_{Guid.NewGuid():N}";

    /// <summary>An attempt on the order that was captured and recorded on it, ready to be refunded.</summary>
    private static async Task<PaymentAttempt> CapturedAsync(IPaymentAttemptStore store, PaymentOrder order)
    {
        var attempt = NewAttempt(order, Start);
        await store.AddAsync(attempt, None);
        attempt.Authorize("rail", Start.AddSeconds(1));
        attempt.Capture(Unique("gp_pay"), Start.AddSeconds(1));
        attempt.MarkRecordedOnOrder(Start.AddSeconds(2));
        await store.SaveAsync(attempt, None);
        return attempt;
    }

    private static async Task<bool> Try(Task save)
    {
        try
        {
            await save;
            return true;
        }
        catch (PaymentAttemptConcurrencyException)
        {
            return false;
        }
    }

    private static PaymentAttempt NewAttempt(PaymentOrder order, DateTimeOffset at, PaymentMethod method = PaymentMethod.Upi) =>
        PaymentAttempt.Start(
            Guid.NewGuid(),
            order.OrderId,
            order.ClientId,
            order.Quote.SourceAmount,
            method == PaymentMethod.Card
                ? new PaymentInstrument(PaymentMethod.Card, "Visa ····1111", "Visa", "1111")
                : new PaymentInstrument(PaymentMethod.Upi, "success@sandbox"),
            at,
            Window);

    /// <summary>A real order in the database for attempts to refer to, from its own merchant.</summary>
    private static async Task<PaymentOrder> OrderAsync(ServiceProvider services)
    {
        var merchant = $"merchant-{Guid.NewGuid():N}";
        await using (var connection = await services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync())
        await using (var command = new NpgsqlCommand(
            "INSERT INTO gateway.api_client (client_id, display_name, key_sha256, role) VALUES (@id, @id, @hash, 'MERCHANT')", connection))
        {
            command.Parameters.AddWithValue("id", merchant);
            command.Parameters.AddWithValue("hash", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(merchant))));
            await command.ExecuteNonQueryAsync();
        }

        var settlement = services.GetRequiredService<SettlementService>();
        var (wallet, _) = await settlement.RegisterAsync(merchant, "ethereum", Address, null, None);
        if (wallet.Status == SettlementWalletStatus.UnderReview)
        {
            await settlement.ApproveAsync(wallet.WalletId, "officer", "Test wallet, approved for this scenario.", None);
        }

        var onRamp = services.GetRequiredService<OnRampService>();
        var quote = await onRamp.CreateQuoteAsync(merchant, new CreateQuoteCommand("INR", 10_000m, "USDC@ethereum"), None);
        return await onRamp.CreateOrderAsync(merchant, new CreateOrderCommand(quote.QuoteId, SandboxCustomerDirectory.Verified), None);
    }
}
