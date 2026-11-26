using FiatCryptoGateway.Application.Alerts;
using FiatCryptoGateway.Application.Configuration;
using FiatCryptoGateway.Application.Ports;
using FiatCryptoGateway.Sandbox;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FiatCryptoGateway.Tests;

public sealed class AlertServiceTests
{
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly AlertProblem HeldDue = new("held:a:due", AlertSeverity.Warning, "Held payment A has waited 19 hours; the limit is 24 hours.");
    private static readonly AlertProblem HeldOverdue = new("held:a:overdue", AlertSeverity.Urgent, "Held payment A has waited 25 hours, past the 24-hour limit.");
    private static readonly AlertProblem Wallet = new("wallet:c", AlertSeverity.Warning, "A new polygon wallet for acme has waited 20 hours for review.");

    private readonly ManualTimeProvider _time = new(Fixtures.Start);
    private readonly FakeSource _source = new();
    private readonly RecordingSender _sender = new();
    private readonly InMemoryAlertLog _log = new();
    private readonly AlertOptions _options = new()
    {
        To = "ops@example.com, compliance@example.com",
        From = "gateway@example.com",
        ConsoleUrl = new Uri("https://pay.example.com/review"),
    };

    [Fact]
    public async Task WithoutRecipients_NothingIsChecked()
    {
        _options.To = string.Empty;
        _source.Problems = [HeldDue];

        Assert.Equal(AlertOutcome.Off, await RunAsync());
        Assert.Empty(_sender.Sent);
        Assert.Equal(0, _source.Checks);
    }

    [Fact]
    public async Task NewProblems_AreEmailedOnce()
    {
        _source.Problems = [HeldDue, Wallet];

        Assert.Equal(AlertOutcome.SentProblems, await RunAsync());
        Assert.Equal(AlertOutcome.NothingNew, await RunAsync());

        var mail = Assert.Single(_sender.Sent);
        Assert.Equal(["ops@example.com", "compliance@example.com"], mail.To);
        Assert.Equal("gateway@example.com", mail.From);
        Assert.Equal("[Gateway Sandbox] 2 new problems need a person", mail.Subject);
        Assert.Contains("New since the last email:\n- " + HeldDue.Text + "\n- " + Wallet.Text, Normalised(mail.Body), StringComparison.Ordinal);
        Assert.Contains("Reviewer console: https://pay.example.com/review", mail.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AProblemThatGetsWorse_IsEmailedAgain_WithWhatIsStillOpen()
    {
        _source.Problems = [HeldDue, Wallet];
        await RunAsync();
        _time.Advance(TimeSpan.FromHours(6));
        _source.Problems = [HeldOverdue, Wallet];

        Assert.Equal(AlertOutcome.SentProblems, await RunAsync());

        var mail = _sender.Sent[^1];
        Assert.Equal("[Gateway Sandbox] URGENT: 1 new problem needs a person", mail.Subject);
        Assert.Contains("New since the last email:\n- URGENT: " + HeldOverdue.Text + "\n\nStill open from before:\n- " + Wallet.Text, Normalised(mail.Body), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenProblems_AreRemindedAfterTwelveHours()
    {
        _source.Problems = [Wallet];
        await RunAsync();

        _time.Advance(TimeSpan.FromHours(11));
        Assert.Equal(AlertOutcome.NothingNew, await RunAsync());
        _time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(AlertOutcome.SentReminder, await RunAsync());
        Assert.Equal(AlertOutcome.NothingNew, await RunAsync());

        Assert.Equal("[Gateway Sandbox] Reminder: 1 problem still open", _sender.Sent[^1].Subject);
        Assert.Contains("Still open:\n- " + Wallet.Text, Normalised(_sender.Sent[^1].Body), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenEverythingClears_ItSaysSoOnce()
    {
        _source.Problems = [Wallet];
        await RunAsync();
        _source.Problems = [];

        Assert.Equal(AlertOutcome.SentAllClear, await RunAsync());
        Assert.Equal(AlertOutcome.NothingNew, await RunAsync());
        Assert.Equal("[Gateway Sandbox] All clear", _sender.Sent[^1].Subject);

        _source.Problems = [Wallet];
        Assert.Equal(AlertOutcome.SentProblems, await RunAsync());
    }

    [Fact]
    public async Task NothingToReport_SendsNothing()
    {
        Assert.Equal(AlertOutcome.NothingNew, await RunAsync());
        Assert.Empty(_sender.Sent);
    }

    [Fact]
    public async Task AFailedSend_IsTriedAgainOnTheNextCheck()
    {
        _source.Problems = [HeldDue];
        _sender.FailNext = true;

        await Assert.ThrowsAsync<InvalidOperationException>(RunAsync);
        Assert.Null(await _log.GetLastAsync(None));

        Assert.Equal(AlertOutcome.SentProblems, await RunAsync());
        Assert.Single(_sender.Sent);
    }

    private static string Normalised(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private Task<AlertOutcome> RunAsync() =>
        new AlertService(
            _source,
            _log,
            _sender,
            new NoLocks(),
            Options.Create(_options),
            Options.Create(new GatewaySafetyOptions()),
            _time,
            NullLogger<AlertService>.Instance).RunAsync(None);

    private sealed class FakeSource : IAlertSource
    {
        public IReadOnlyList<AlertProblem> Problems { get; set; } = [];

        public int Checks { get; private set; }

        public Task<IReadOnlyList<AlertProblem>> FindAsync(CancellationToken cancellationToken)
        {
            Checks++;
            return Task.FromResult(Problems);
        }
    }

    private sealed class RecordingSender : IAlertSender
    {
        public List<AlertMessage> Sent { get; } = [];

        public bool FailNext { get; set; }

        public Task SendAsync(AlertMessage message, CancellationToken cancellationToken)
        {
            if (FailNext)
            {
                FailNext = false;
                throw new InvalidOperationException("The mail server is unreachable.");
            }

            Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class NoLocks : IOrderLocks
    {
        public Task<IAsyncDisposable> AcquireAsync(Guid orderId, CancellationToken cancellationToken) =>
            Task.FromResult<IAsyncDisposable>(new Released());

        private sealed class Released : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
