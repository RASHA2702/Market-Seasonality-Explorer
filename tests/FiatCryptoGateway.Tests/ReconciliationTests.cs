using System.Globalization;
using FiatCryptoGateway.Application;
using FiatCryptoGateway.Application.OnRamp;
using FiatCryptoGateway.Application.Ports;
using FiatCryptoGateway.Application.Reconciliation;
using FiatCryptoGateway.Application.Settlement;
using FiatCryptoGateway.Domain;
using FiatCryptoGateway.Domain.Assets;
using FiatCryptoGateway.Domain.Ledger;
using FiatCryptoGateway.Domain.Orders;
using FiatCryptoGateway.Sandbox;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FiatCryptoGateway.Tests;

public sealed class ReconcilerTests
{
    private const string Psp = "test-psp";
    private static readonly DateOnly Day = new(2026, 11, 24);
    private static readonly DateTimeOffset WindowStart = new(2026, 11, 23, 18, 30, 0, TimeSpan.Zero); // 00:00 IST
    private static readonly DateTimeOffset WindowEnd = new(2026, 11, 24, 18, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Noon = new(2026, 11, 24, 6, 30, 0, TimeSpan.Zero);           // 12:00 IST

    [Fact]
    public void ReportMatchingTheBooks_IsClean()
    {
        var result = Compare(
            [Line(EntryKinds.FiatReceived, "pay_1", 10_000m), Line(EntryKinds.FiatRefunded, "rfd_2", 500m)],
            [Entry(EntryKinds.FiatReceived, "pay_1", 10_000m), Entry(EntryKinds.FiatRefunded, "rfd_2", 500m)]);

        Assert.Equal((2, 2), (result.LedgerEntries, result.Matched));
        Assert.Empty(result.Discrepancies);
    }

    [Fact]
    public void EachKindOfDifference_IsReportedWithWhatEachSideShows()
    {
        var result = Compare(
            [
                Line(EntryKinds.FiatReceived, "pay_missing_in_books", 1_000m),
                Line(EntryKinds.FiatReceived, "pay_amount", 2_000m),
                Line(EntryKinds.FiatReceived, "pay_twice_in_report", 3_000m),
                Line(EntryKinds.FiatReceived, "pay_twice_in_report", 3_000m),
                Line(EntryKinds.FiatReceived, "pay_twice_in_books", 4_000m),
                Line(EntryKinds.FiatReceived, "pay_ok", 5_000m),
            ],
            [
                Entry(EntryKinds.FiatReceived, "pay_amount", 1_999m),
                Entry(EntryKinds.FiatReceived, "pay_twice_in_report", 3_000m),
                Entry(EntryKinds.FiatReceived, "pay_twice_in_books", 4_000m),
                Entry(EntryKinds.FiatReceived, "pay_twice_in_books", 4_000m),
                Entry(EntryKinds.FiatReceived, "pay_ok", 5_000m),
                Entry(EntryKinds.FiatRefunded, "rfd_missing_in_report", 600m),
            ]);

        Assert.Equal(1, result.Matched);
        Assert.Equal(
            [
                (DiscrepancyType.MissingInLedger, "pay_missing_in_books", (decimal?)1_000m, (decimal?)null),
                (DiscrepancyType.MissingInReport, "rfd_missing_in_report", null, 600m),
                (DiscrepancyType.AmountMismatch, "pay_amount", 2_000m, 1_999m),
                (DiscrepancyType.DuplicateInLedger, "pay_twice_in_books", 4_000m, 4_000m),
                (DiscrepancyType.DuplicateInReport, "pay_twice_in_report", 3_000m, null),
            ],
            result.Discrepancies.Select(d => (d.Type, d.Reference, d.ReportAmount?.Amount, d.LedgerAmount?.Amount)));
    }

    [Fact]
    public void MovementTheReportPlacesOnAnotherDay_StillMatchesByReference()
    {
        // The books have it a minute after midnight (by the provider's clock); the report lists it the day before.
        var result = Compare([Line(EntryKinds.FiatReceived, "pay_1", 100m)], [Entry(EntryKinds.FiatReceived, "pay_1", 100m, WindowEnd.AddMinutes(1))]);

        Assert.Equal((0, 1), (result.LedgerEntries, result.Matched));
        Assert.Empty(result.Discrepancies);
    }

    [Fact]
    public void EntriesFromOtherDaysOrProviders_AreNotExpectedInTheReport()
    {
        var result = Compare(
            [],
            [
                Entry(EntryKinds.FiatReceived, "pay_day_before", 100m, WindowStart.AddTicks(-1)),
                Entry(EntryKinds.FiatReceived, "pay_day_after", 100m, WindowEnd),
                Entry(EntryKinds.FiatReceived, "pay_other_provider", 100m, provider: "other-psp"),
            ]);

        Assert.Equal((0, 0), (result.LedgerEntries, result.Matched));
        Assert.Empty(result.Discrepancies);
    }

    [Fact]
    public void EqualAmounts_MatchWhateverTheirDecimalPlaces()
    {
        Assert.Equal(1, Compare([Line(EntryKinds.FiatReceived, "pay_1", 100.00m)], [Entry(EntryKinds.FiatReceived, "pay_1", 100m)]).Matched);
    }

    [Fact]
    public void SameNumberInAnotherCurrency_IsAMismatch()
    {
        var report = new SettlementReport(Psp, Day, [new SettlementLine(EntryKinds.FiatReceived, "pay_1", null, new Money(100m, Fixtures.Usd), Noon)]);

        var result = Reconciler.Compare(report, [Entry(EntryKinds.FiatReceived, "pay_1", 100m)], WindowStart, WindowEnd);

        Assert.Equal(DiscrepancyType.AmountMismatch, Assert.Single(result.Discrepancies).Type);
    }

    private static ReconciliationResult Compare(IReadOnlyList<SettlementLine> lines, IReadOnlyList<JournalEntry> entries) =>
        Reconciler.Compare(new SettlementReport(Psp, Day, lines), entries, WindowStart, WindowEnd);

    private static SettlementLine Line(string kind, string reference, decimal inr) =>
        new(kind, reference, null, new Money(inr, Fixtures.Inr), Noon);

    private static JournalEntry Entry(string kind, string reference, decimal inr, DateTimeOffset? at = null, string provider = Psp)
    {
        var amount = new Money(inr, Fixtures.Inr);
        return new JournalEntryBuilder()
            .Debit(LedgerAccount.PspClearing(provider, Fixtures.Inr.Id), amount)
            .Credit(LedgerAccount.FundsHeldForCustomers(Fixtures.Inr.Id), amount)
            .Build(kind, Guid.NewGuid(), Noon, new ExternalReference(provider, reference, amount, at ?? Noon));
    }
}

public sealed class BusinessDayTests
{
    private static readonly TimeZoneInfo India = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");

    [Fact]
    public void IndianDay_RunsFromMidnightToMidnightIndiaTime()
    {
        var (start, end) = BusinessDay.Window(new DateOnly(2026, 11, 24), India);

        Assert.Equal(new DateTimeOffset(2026, 11, 23, 18, 30, 0, TimeSpan.Zero), start);
        Assert.Equal(new DateTimeOffset(2026, 11, 24, 18, 30, 0, TimeSpan.Zero), end);
    }

    [Theory]
    [InlineData("2026-11-24T18:29:59Z", "2026-11-24")]
    [InlineData("2026-11-24T18:30:00Z", "2026-11-25")] // midnight in India
    public void Day_FollowsIndiaTime(string utc, string expected)
    {
        Assert.Equal(
            DateOnly.ParseExact(expected, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            BusinessDay.Of(DateTimeOffset.Parse(utc, CultureInfo.InvariantCulture), India));
    }

    [Theory]
    [InlineData("TimeZone", "Mars/Olympus_Mons")]
    [InlineData("RunAfterHour", "24")]
    [InlineData("LookbackDays", "0")]
    [InlineData("CheckIntervalMinutes", "0")]
    public void InvalidReconciliationSettings_AreRefused(string key, string value)
    {
        var options = new ReconciliationOptions();
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [key] = value }).Build().Bind(options);

        Assert.True(new ReconciliationOptionsValidator().Validate(null, options).Failed);
        Assert.True(new ReconciliationOptionsValidator().Validate(null, new ReconciliationOptions()).Succeeded);
    }
}

public sealed class ReconciliationServiceTests
{
    private const string Address = "0x1111111111111111111111111111111111111111";
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly DateTimeOffset Start = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero); // 17:30 IST
    private static readonly DateOnly Day = new(2026, 11, 24);

    [Fact]
    public async Task SandboxDay_MatchesPaymentsAndRefunds_AndFlagsAPaymentWhoseWebhookNeverArrived()
    {
        var time = new ManualTimeProvider(Start);
        await using var services = BuildServices(time);
        var onRamp = services.GetRequiredService<OnRampService>();
        var webhooks = services.GetRequiredService<SandboxFiatWebhookHandler>();

        var paid = await CreateOrderAsync(onRamp, services);
        var (body, signature) = webhooks.CreateSignedPaymentEvent(paid);
        Assert.Equal(WebhookResult.Accepted, await webhooks.HandleAsync(body, signature, None));

        var late = await CreateOrderAsync(onRamp, services); // paid after its deadline, so refunded
        (body, signature) = webhooks.CreateSignedPaymentEvent(late, capturedAt: late.PaymentDeadline.AddMinutes(1));
        Assert.Equal(WebhookResult.Accepted, await webhooks.HandleAsync(body, signature, None));
        Assert.Equal(OrderStatus.Refunded, (await onRamp.GetOrderAsync(Fixtures.ClientId, late.OrderId, None)).Status);

        var lost = await CreateOrderAsync(onRamp, services); // the provider took the money, but its webhook never arrived
        webhooks.CreateSignedPaymentEvent(lost);

        time.Advance(TimeSpan.FromHours(13)); // 06:30 IST the next day
        var run = Assert.Single(await services.GetRequiredService<ReconciliationService>().RunAsync(Day, None));

        Assert.Equal((SandboxFiatProvider.ProviderName, 4, 3, 3), (run.Provider, run.ReportLines, run.LedgerEntries, run.Matched));
        var discrepancy = Assert.Single(run.Discrepancies);
        Assert.Equal(
            (DiscrepancyType.MissingInLedger, EntryKinds.FiatReceived, (Guid?)lost.OrderId, "10000.00"),
            (discrepancy.Type, discrepancy.Kind, discrepancy.OrderId, discrepancy.ReportAmount!.ToFixedString()));
        Assert.Same(run, Assert.Single(await services.GetRequiredService<IReconciliationStore>().ListAsync(Day, 10, None)));
    }

    [Fact]
    public async Task DayThatHasNotEnded_IsRefused()
    {
        await using var services = BuildServices(new ManualTimeProvider(Start));

        var exception = await Assert.ThrowsAsync<GatewayRuleException>(() => services.GetRequiredService<ReconciliationService>().RunAsync(Day, None));

        Assert.Equal("DAY_NOT_FINISHED", exception.Code);
    }

    [Fact]
    public async Task DailySchedule_RunsEachRecentDayOnceAfterTheMorningCutoff()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 11, 24, 23, 30, 0, TimeSpan.Zero)); // 05:00 IST on the 25th
        await using var services = BuildServices(time);
        var reconciliation = services.GetRequiredService<ReconciliationService>();

        Assert.Equal(0, await reconciliation.RunDueAsync(None)); // reports may not be ready before 06:00
        time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(2, await reconciliation.RunDueAsync(None)); // the 24th, and the 23rd again
        Assert.Equal(0, await reconciliation.RunDueAsync(None)); // already done today
        time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(2, await reconciliation.RunDueAsync(None)); // the 25th, and the 24th again

        var runs = await services.GetRequiredService<IReconciliationStore>().ListAsync(null, 10, None);
        Assert.Equal(["11-23", "11-24", "11-24", "11-25"], runs.Select(run => run.Day.ToString("MM-dd", CultureInfo.InvariantCulture)).Order(StringComparer.Ordinal));
    }

    private static async Task<PaymentOrder> CreateOrderAsync(OnRampService onRamp, IServiceProvider services)
    {
        await services.GetRequiredService<SettlementService>().RegisterAsync(Fixtures.ClientId, "ethereum", Address, null, None);
        var quote = await onRamp.CreateQuoteAsync(Fixtures.ClientId, new CreateQuoteCommand("INR", 10_000m, "USDC@ethereum"), None);
        return await onRamp.CreateOrderAsync(Fixtures.ClientId, new CreateOrderCommand(quote.QuoteId, SandboxCustomerDirectory.Verified), None);
    }

    private static ServiceProvider BuildServices(TimeProvider time)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:Settlement:CoolingOffHours"] = "0",
                ["Gateway:Fiat:Provider"] = "SandboxPartner", // these tests reconcile the simulated partner's report
            })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton(time);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddGatewayCore(configuration);
        services.AddSandboxProviders(configuration);
        services.AddInMemoryStores();
        return services.BuildServiceProvider(validateScopes: true);
    }
}
