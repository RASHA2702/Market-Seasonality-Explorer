using System.Globalization;
using FiatCryptoGateway.Application.Alerts;
using FiatCryptoGateway.Application.Configuration;
using FiatCryptoGateway.Application.Payments;
using FiatCryptoGateway.Application.Ports;
using FiatCryptoGateway.Domain.Ledger;
using FiatCryptoGateway.Domain.Orders;
using FiatCryptoGateway.Sandbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FiatCryptoGateway.Tests;

/// <summary>
/// The alert check watching the gateway's own processor: a payment stuck between the network and its order is urgent,
/// because a customer's money is involved and the merchant has not been paid. The stuck states are made the way they
/// happen, with a network that will not capture, or a captured payment the order has not recorded.
/// </summary>
public sealed class StuckPaymentAlertTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static readonly TimeSpan JustUnderThirtyMinutes = TimeSpan.FromMinutes(29) + TimeSpan.FromSeconds(59);

    [Fact]
    public async Task AnApprovalThatCannotBeCaptured_IsReportedOnceStuckForThirtyMinutes_AsUrgent()
    {
        using var h = new ProcessorHarness();
        var order = await h.CreateOrderAsync();
        h.UpiRail.FailCaptures = int.MaxValue;
        var approved = await h.ApproveAsync(order, await h.StartUpiAsync(order));

        h.Time.Advance(JustUnderThirtyMinutes);
        Assert.Empty(await PaymentProblemsAsync(h)); // a second short

        h.Time.Advance(TimeSpan.FromSeconds(1));
        var problem = Assert.Single(await PaymentProblemsAsync(h));
        Assert.Equal($"payment:uncaptured:{approved.AttemptId:N}", problem.Key);
        Assert.Equal(AlertSeverity.Urgent, problem.Severity);
        Assert.Equal(
            $"A payment for order {Short(order)} ({ProcessorHarness.Client}) was approved 30 minutes ago and could not be captured. " +
            "The gateway keeps trying, and gives the approval up after 24 hours.",
            problem.Text);
    }

    [Fact]
    public async Task ACapturedPayment_TheOrderHasNotRecorded_IsReportedOnceStuckForThirtyMinutes_AsUrgent()
    {
        using var h = new ProcessorHarness();
        var order = await h.CreateOrderAsync();
        var captured = await h.SeedCapturedAsync(order);

        h.Time.Advance(JustUnderThirtyMinutes);
        Assert.Empty(await PaymentProblemsAsync(h)); // a second short

        h.Time.Advance(TimeSpan.FromSeconds(1));
        var problem = Assert.Single(await PaymentProblemsAsync(h));
        Assert.Equal($"payment:unrecorded:{captured.AttemptId:N}", problem.Key);
        Assert.Equal(AlertSeverity.Urgent, problem.Severity);
        Assert.Equal(
            $"Money for order {Short(order)} ({ProcessorHarness.Client}) was taken 30 minutes ago, but the order has not recorded it. The gateway keeps trying.",
            problem.Text);
    }

    [Fact]
    public async Task OnlyTheCapturedPayments_StuckLongEnough_AreNamed_AndAnOlderOneDoesNotHideANewerOne()
    {
        using var h = new ProcessorHarness();
        var first = await h.SeedCapturedAsync(await h.CreateOrderAsync());
        h.Time.Advance(TimeSpan.FromMinutes(20));
        var second = await h.SeedCapturedAsync(await h.CreateOrderAsync());
        h.Time.Advance(TimeSpan.FromMinutes(11)); // the first has waited 31 minutes, the second 11

        var problem = Assert.Single(await PaymentProblemsAsync(h));
        Assert.Equal($"payment:unrecorded:{first.AttemptId:N}", problem.Key);

        h.Time.Advance(TimeSpan.FromMinutes(19)); // now the second has waited 30 minutes too
        Assert.Equal(
            [$"payment:unrecorded:{first.AttemptId:N}", $"payment:unrecorded:{second.AttemptId:N}"],
            (await PaymentProblemsAsync(h)).Select(found => found.Key));
    }

    [Fact]
    public async Task AnApprovalGivenUp_IsReportedForSevenDays_AfterItWasGivenUp()
    {
        using var h = new ProcessorHarness();
        var order = await h.CreateOrderAsync();
        h.UpiRail.FailCaptures = int.MaxValue;
        var approved = await h.ApproveAsync(order, await h.StartUpiAsync(order));

        h.Time.Advance(TimeSpan.FromHours(25));
        Assert.Equal(new SweepResult(0, 0, 0, 1, 0), await h.Processor.SweepAsync(None)); // gives the approval up

        // It was reported as stuck; now it is reported as given up, once.
        var problem = Assert.Single(await PaymentProblemsAsync(h));
        Assert.Equal($"payment:lapsed:{approved.AttemptId:N}", problem.Key);
        Assert.Equal(AlertSeverity.Urgent, problem.Severity);
        Assert.Equal(
            $"An approval for order {Short(order)} ({ProcessorHarness.Client}) was given up after 24 hours without being captured. " +
            "Check the payment network's own report to be sure the customer was not debited.",
            problem.Text);

        h.Time.Advance(TimeSpan.FromDays(7));
        Assert.Single(await PaymentProblemsAsync(h)); // seven days exactly after it was given up
        h.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Empty(await PaymentProblemsAsync(h));
    }

    [Fact]
    public async Task WhenTheSweepFinishesTheJob_TheProblemClears()
    {
        using var h = new ProcessorHarness();
        var order = await h.CreateOrderAsync();
        h.UpiRail.FailCaptures = 1; // the capture right after approval fails; the sweep's retry works
        await h.ApproveAsync(order, await h.StartUpiAsync(order));
        h.Time.Advance(TimeSpan.FromMinutes(31));
        Assert.Single(await PaymentProblemsAsync(h));

        await h.Processor.SweepAsync(None);

        Assert.Empty(await PaymentProblemsAsync(h));
        Assert.Equal(1, await h.LedgerEntriesAsync(order, EntryKinds.FiatReceived));
    }

    [Fact]
    public async Task TheThresholds_AreSettings()
    {
        using var h = new ProcessorHarness(new Dictionary<string, string?>
        {
            ["Gateway:Alerts:StuckPaymentAfterMinutes"] = "10",
            ["Gateway:Alerts:LapsedApprovalReportDays"] = "1",
        });
        var stuck = await h.CreateOrderAsync();
        h.UpiRail.FailCaptures = int.MaxValue;
        await h.ApproveAsync(stuck, await h.StartUpiAsync(stuck));

        h.Time.Advance(TimeSpan.FromMinutes(9) + TimeSpan.FromSeconds(59));
        Assert.Empty(await PaymentProblemsAsync(h));
        h.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Single(await PaymentProblemsAsync(h));

        h.Time.Advance(TimeSpan.FromHours(25) - TimeSpan.FromMinutes(10));
        await h.Processor.SweepAsync(None); // given up
        h.Time.Advance(TimeSpan.FromDays(1));
        Assert.Single(await PaymentProblemsAsync(h));
        h.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Empty(await PaymentProblemsAsync(h));
    }

    [Fact]
    public async Task TheLapseTime_InTheText_IsTheProcessorsSetting()
    {
        using var h = new ProcessorHarness(new Dictionary<string, string?> { ["Gateway:Processor:AuthorizationLapseHours"] = "2" });
        var order = await h.CreateOrderAsync();
        h.UpiRail.FailCaptures = int.MaxValue;
        await h.ApproveAsync(order, await h.StartUpiAsync(order));
        h.Time.Advance(TimeSpan.FromMinutes(31));

        var stuck = Assert.Single(await PaymentProblemsAsync(h));
        Assert.EndsWith("The gateway keeps trying, and gives the approval up after 2 hours.", stuck.Text, StringComparison.Ordinal);

        h.Time.Advance(TimeSpan.FromHours(2));
        await h.Processor.SweepAsync(None);
        var lapsed = Assert.Single(await PaymentProblemsAsync(h));
        Assert.Contains("was given up after 2 hours without being captured", lapsed.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThePaymentsAreNamedByShortOrderAndMerchantOnly_NeverByWhatTheCustomerUsed()
    {
        using var h = new ProcessorHarness();
        var order = await h.CreateOrderAsync();
        h.UpiRail.FailCaptures = int.MaxValue;
        await h.ApproveAsync(order, await h.StartUpiAsync(order));
        h.Time.Advance(TimeSpan.FromHours(25));
        await h.Processor.SweepAsync(None); // gives the first approval up

        var stuckApproval = await h.CreateOrderAsync();
        await h.ApproveAsync(stuckApproval, await h.StartUpiAsync(stuckApproval));
        await h.SeedCapturedAsync(await h.CreateOrderAsync());
        h.Time.Advance(TimeSpan.FromMinutes(31));
        await h.SeedCapturedAsync(await h.CreateOrderAsync()); // captured a moment ago: not stuck, and it must not hide the problems after it

        var problems = await PaymentProblemsAsync(h);

        Assert.Equal(
            ["payment:uncaptured:", "payment:unrecorded:", "payment:lapsed:"],
            problems.Select(problem => string.Join(':', problem.Key.Split(':')[..2]) + ":"));
        Assert.All(problems, problem =>
        {
            Assert.DoesNotContain(SandboxCustomerDirectory.Verified, problem.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(SandboxTestInstruments.SuccessfulUpiAddress, problem.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(order.OrderId.ToString(), problem.Text, StringComparison.OrdinalIgnoreCase); // the whole ID is not shown
            Assert.DoesNotContain("@", problem.Text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task AStuckPayment_IsEmailedAsUrgent_AndClearedWithAnAllClear()
    {
        using var h = new ProcessorHarness();
        var order = await h.CreateOrderAsync();
        h.UpiRail.FailCaptures = int.MaxValue;
        await h.ApproveAsync(order, await h.StartUpiAsync(order));
        h.Time.Advance(TimeSpan.FromMinutes(30));
        var sender = new RecordingSender();
        var service = new AlertService(
            h.Services.GetRequiredService<AlertChecks>(),
            new InMemoryAlertLog(),
            sender,
            h.Services.GetRequiredService<IOrderLocks>(),
            Options.Create(new AlertOptions { To = "ops@example.com, second@example.com", From = "gateway@example.com" }),
            Options.Create(new GatewaySafetyOptions()),
            h.Time,
            NullLogger<AlertService>.Instance);

        Assert.Equal(AlertOutcome.SentProblems, await service.RunAsync(None));
        Assert.Equal(AlertOutcome.NothingNew, await service.RunAsync(None));

        var urgent = Assert.Single(sender.Sent);
        Assert.Equal(["ops@example.com", "second@example.com"], urgent.To);
        Assert.Equal("[Gateway Sandbox] URGENT: 1 new problem needs a person", urgent.Subject);
        Assert.Contains($"order {Short(order)} ({ProcessorHarness.Client}) was approved 30 minutes ago and could not be captured", urgent.Body, StringComparison.Ordinal);

        h.UpiRail.FailCaptures = 0;
        await h.Processor.SweepAsync(None);

        Assert.Equal(AlertOutcome.SentAllClear, await service.RunAsync(None));
        Assert.Equal("[Gateway Sandbox] All clear", sender.Sent[^1].Subject);
        Assert.Contains("no payment is stuck or was given up", sender.Sent[^1].Body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheEmailLog_CountsStuckPayments_ApartFromOtherProblems()
    {
        Assert.Equal(
            "1 held payment, 2 stuck payments, 1 failed notification",
            AlertKeys.Describe(["payment:uncaptured:a", "held:b:overdue", "payment:lapsed:c", "webhook:d"]));
        Assert.Equal("1 stuck payment", AlertKeys.Describe(["payment:unrecorded:e"]));
    }

    [Theory]
    [InlineData(4, 7, false)]
    [InlineData(5, 7, true)]
    [InlineData(1_440, 7, true)]
    [InlineData(1_441, 7, false)]
    [InlineData(30, 0, false)]
    [InlineData(30, 1, true)]
    [InlineData(30, 90, true)]
    [InlineData(30, 91, false)]
    public void TheThresholds_MustBeInRange(int stuckMinutes, int lapsedDays, bool accepted)
    {
        var result = new AlertOptionsValidator().Validate(null, new AlertOptions { StuckPaymentAfterMinutes = stuckMinutes, LapsedApprovalReportDays = lapsedDays });

        Assert.Equal(accepted, result.Succeeded);
        if (!accepted)
        {
            Assert.Contains("StuckPaymentAfterMinutes", string.Join(' ', result.Failures!), StringComparison.Ordinal);
        }
    }

    private static async Task<List<AlertProblem>> PaymentProblemsAsync(ProcessorHarness h) =>
        [.. (await h.Services.GetRequiredService<AlertChecks>().FindAsync(None)).Where(problem => problem.Key.StartsWith("payment:", StringComparison.Ordinal))];

    private static string Short(PaymentOrder order) => order.OrderId.ToString("N", CultureInfo.InvariantCulture)[..8].ToUpperInvariant();

    private sealed class RecordingSender : IAlertSender
    {
        public List<AlertMessage> Sent { get; } = [];

        public Task SendAsync(AlertMessage message, CancellationToken cancellationToken)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }
    }
}
