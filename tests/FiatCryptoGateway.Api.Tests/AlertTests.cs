using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using FiatCryptoGateway.Api.Alerts;
using FiatCryptoGateway.Api.Contracts;
using FiatCryptoGateway.Application.Alerts;
using FiatCryptoGateway.Application.Ports;
using FiatCryptoGateway.Application.Reconciliation;
using FiatCryptoGateway.Sandbox;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FiatCryptoGateway.Api.Tests;

public sealed class AlertTests(GatewayApiFactory factory) : IClassFixture<GatewayApiFactory>
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task RealProblems_AreEmailed_EscalatedAndThenCleared()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 26, 6, 0, 0, TimeSpan.Zero));
        var sender = new RecordingAlertSender();
        using var host = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:Alerts:To", "ops@example.com");
            builder.UseSetting("Gateway:Alerts:From", "gateway@example.com");
            builder.UseSetting("Gateway:Alerts:Smtp:Host", "127.0.0.1");
            builder.UseSetting("Gateway:Alerts:Smtp:Security", "None");
            builder.UseSetting("Gateway:Alerts:ReviewWarnAfterHours", "0");
            builder.UseSetting("Gateway:Alerts:BackupMaxAgeHours", "26");
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<TimeProvider>(clock);
                services.AddSingleton<IAlertSender>(sender);

                // The test runs each check itself, so the background worker must not run one in between.
                services.Remove(services.Single(service => service.ImplementationType == typeof(AlertWorker)));
            });
        });
        using var merchant = host.CreateClient();
        merchant.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GatewayApiFactory.ClientAKey);
        using var reviewer = await host.SignInReviewerAsync();
        var order = await ApiFlows.CreateOrderAsync(merchant, reviewer, SandboxWalletScreening.HighRiskAddress);
        await ApiFlows.PostAsync<OrderResponse>(merchant, $"/v1/sandbox/orders/{order.OrderId}/pay", new { });
        var alerts = host.Services.GetRequiredService<AlertService>();
        var shortId = order.OrderId.ToString("N")[..8].ToUpperInvariant();

        Assert.Equal(AlertOutcome.SentProblems, await alerts.RunAsync(None));
        var first = sender.Sent[^1];
        Assert.Equal("[Gateway Sandbox] 1 new problem needs a person", first.Subject);
        Assert.Contains($"- Held payment {shortId} for client-a has waited 0 hours; the limit is 24 hours.", first.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(SandboxCustomerDirectory.Verified, first.Body, StringComparison.Ordinal); // no customer details

        // A day later: the payment is past its limit, no backup has succeeded, a notification failed, and the
        // provider reports money the books do not have.
        clock.Advance(TimeSpan.FromHours(27));
        var outbox = host.Services.GetRequiredService<IOutbox>();
        var notification = (await outbox.ClaimDueAsync(clock.GetUtcNow(), TimeSpan.FromMinutes(5), 1, None))[0];
        await outbox.MarkDeadAsync(notification.MessageId, "HTTP_500", clock.GetUtcNow(), None);
        var reconciliation = host.Services.GetRequiredService<IReconciliationStore>();
        var day = new DateOnly(2026, 11, 26);
        await reconciliation.SaveAsync(Run(day, clock.GetUtcNow(), new Discrepancy(DiscrepancyType.MissingInLedger, "FIAT_RECEIVED", "pay_123", null, null, null)), None);

        Assert.Equal(AlertOutcome.SentProblems, await alerts.RunAsync(None));
        var second = sender.Sent[^1];
        Assert.Equal("[Gateway Sandbox] URGENT: 4 new problems need a person", second.Subject);
        foreach (var expected in new[]
        {
            $"- URGENT: Held payment {shortId} for client-a has waited 27 hours, past the 24-hour limit.",
            "- URGENT: Reconciliation of 2026-11-26 (sandbox-fiat) found 1 difference, including money the provider reports that is not in the books.",
            $"- A {notification.EventType} notification to client-a was given up after 1 attempt (HTTP_500).",
            "- URGENT: No database backup has succeeded since the gateway started 27 hours ago.",
        })
        {
            Assert.Contains(expected, second.Body, StringComparison.Ordinal);
        }

        Assert.Equal(AlertOutcome.NothingNew, await alerts.RunAsync(None));

        // Everything is dealt with: decided, reconciled again, sent again, backed up.
        using var nextMorning = await host.SignInReviewerAsync();
        await ApiFlows.PostAsync<ReviewOrderResponse>(nextMorning, $"/v1/review/orders/{order.OrderId}/approve", new { reason = "Checked with the customer." });
        await reconciliation.SaveAsync(Run(day, clock.GetUtcNow().AddMinutes(1)), None);
        Assert.True(await outbox.RetryAsync(notification.MessageId, clock.GetUtcNow(), None));
        await host.Services.GetRequiredService<IBackupRunStore>().RecordAsync(
            new BackupRun(clock.GetUtcNow(), true, "gateway-20261127T090000Z.dump.age", null), None);

        Assert.Equal(AlertOutcome.SentAllClear, await alerts.RunAsync(None));
        Assert.Equal("[Gateway Sandbox] All clear", sender.Sent[^1].Subject);
    }

    [Fact]
    public async Task Email_IsSentThroughTheMailServer()
    {
        await using var server = FakeSmtpServer.Start();
        var smtp = new SmtpAlertSender(Options.Create(new AlertOptions
        {
            Smtp = new AlertSmtpOptions { Host = "127.0.0.1", Port = server.Port, Security = "None" },
        }));

        await smtp.SendAsync(
            new AlertMessage(
                ["ops@example.com", "compliance@example.com"],
                "gateway@example.com",
                "[Gateway Sandbox] 1 new problem needs a person",
                "New since the last email:\n- Held payment 1496B22C for acme has waited 19 hours; the limit is 24 hours.\n"),
            None);

        var conversation = await server.Received;
        Assert.Contains("MAIL FROM:<gateway@example.com>", conversation, StringComparison.Ordinal);
        Assert.Contains("RCPT TO:<ops@example.com>", conversation, StringComparison.Ordinal);
        Assert.Contains("RCPT TO:<compliance@example.com>", conversation, StringComparison.Ordinal);
        Assert.Contains("Subject: [Gateway Sandbox] 1 new problem needs a person", conversation, StringComparison.Ordinal);
        Assert.Contains("- Held payment 1496B22C for acme has waited 19 hours; the limit is 24 hours.", conversation, StringComparison.Ordinal);
    }

    private static ReconciliationRun Run(DateOnly day, DateTimeOffset ranAt, params Discrepancy[] differences) =>
        new(Guid.NewGuid(), "sandbox-fiat", day, ranAt.AddDays(-2), ranAt.AddDays(-1), ranAt, 1, 1, 1 - differences.Length, differences);

    private sealed class RecordingAlertSender : IAlertSender
    {
        public List<AlertMessage> Sent { get; } = [];

        public Task SendAsync(AlertMessage message, CancellationToken cancellationToken)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    /// <summary>Just enough of a mail server to receive one message over plain SMTP and hand back the conversation.</summary>
    private sealed class FakeSmtpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

        private FakeSmtpServer()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Received = ServeOneAsync();
        }

        public int Port { get; }

        public Task<string> Received { get; }

        public static FakeSmtpServer Start() => new();

        public ValueTask DisposeAsync()
        {
            _listener.Stop();
            _listener.Dispose();
            return ValueTask.CompletedTask;
        }

        private async Task<string> ServeOneAsync()
        {
            using var client = await _listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII);
            await using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
            var conversation = new StringBuilder();
            await writer.WriteLineAsync("220 fake ESMTP");
            while (await reader.ReadLineAsync() is { } line)
            {
                conversation.AppendLine(line);
                if (line.StartsWith("DATA", StringComparison.OrdinalIgnoreCase))
                {
                    await writer.WriteLineAsync("354 go ahead");
                    while (await reader.ReadLineAsync() is { } data && data != ".")
                    {
                        conversation.AppendLine(data);
                    }

                    await writer.WriteLineAsync("250 queued");
                }
                else if (line.StartsWith("QUIT", StringComparison.OrdinalIgnoreCase))
                {
                    await writer.WriteLineAsync("221 bye");
                    break;
                }
                else
                {
                    await writer.WriteLineAsync("250 ok");
                }
            }

            return conversation.ToString();
        }
    }
}
