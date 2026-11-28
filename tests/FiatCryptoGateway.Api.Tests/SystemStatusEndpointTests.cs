using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FiatCryptoGateway.Api.Alerts;
using FiatCryptoGateway.Api.Contracts;
using FiatCryptoGateway.Application.Alerts;
using FiatCryptoGateway.Application.Payments;
using FiatCryptoGateway.Application.Ports;
using FiatCryptoGateway.Domain.Payments;
using FiatCryptoGateway.Sandbox;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace FiatCryptoGateway.Api.Tests;

public sealed class SystemStatusEndpointTests(GatewayApiFactory factory) : IClassFixture<GatewayApiFactory>
{
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly Uri SystemPath = new("/v1/review/system", UriKind.Relative);

    [Fact]
    public async Task Reviewer_SeesBackupsOpenProblemsAndAlertEmails()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 26, 6, 0, 0, TimeSpan.Zero));
        var sender = new CapturingSender();
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
                services.Remove(services.Single(service => service.ImplementationType == typeof(AlertWorker)));
            });
        });
        using var merchant = host.CreateClient();
        merchant.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GatewayApiFactory.ClientAKey);
        using var reviewer = await host.SignInReviewerAsync();
        var backups = host.Services.GetRequiredService<IBackupRunStore>();

        // Nothing has happened yet: backups are watched but have not run, nothing is open, no email has been sent.
        var quiet = await GetAsync(reviewer);
        Assert.Equal(clock.GetUtcNow(), quiet.CheckedAt);
        Assert.Equal(("WaitingForFirst", true, 26), (quiet.Backups.State, quiet.Backups.Watched, quiet.Backups.MaxAgeHours));
        Assert.Empty(quiet.Backups.Recent);
        Assert.Empty(quiet.Alerts.Open);
        Assert.Empty(quiet.Alerts.RecentEmails);
        Assert.Equal((true, 1, 5), (quiet.Alerts.EmailOn, quiet.Alerts.Recipients, quiet.Alerts.CheckIntervalMinutes));

        // A risky wallet is waiting: it is an open problem, and the alert email about it shows up in the history.
        using var registered = await merchant.PostAsJsonAsync(
            new Uri("/v1/settlement/wallets", UriKind.Relative), new { network = "ethereum", address = SandboxWalletScreening.HighRiskAddress });
        registered.EnsureSuccessStatusCode();
        var waiting = await GetAsync(reviewer);
        var problem = Assert.Single(waiting.Alerts.Open);
        Assert.Equal(("Warning", "A new ethereum wallet for client-a has waited 0 hours for review."), (problem.Severity, problem.Text));

        await host.Services.GetRequiredService<AlertService>().RunAsync(None);
        var emailed = Assert.Single((await GetAsync(reviewer)).Alerts.RecentEmails);
        Assert.Equal(("Problems", 1, "1 wallet waiting for review"), (emailed.Kind, emailed.Problems, emailed.Summary));

        // A backup succeeds, then one fails, then one succeeds again.
        await backups.RecordAsync(new BackupRun(clock.GetUtcNow(), true, "gateway-20261126T060000Z.dump.age", null, 85_000), None);
        var ok = await GetAsync(reviewer);
        Assert.Equal("Ok", ok.Backups.State);
        Assert.Equal(("gateway-20261126T060000Z.dump.age", 85_000L), (ok.Backups.LatestSuccess!.File, ok.Backups.LatestSuccess.Bytes));

        clock.Advance(TimeSpan.FromMinutes(10));
        await backups.RecordAsync(new BackupRun(clock.GetUtcNow(), false, null, "Exit status 3; the backup container's log has the details."), None);
        var failed = await GetAsync(reviewer);
        Assert.Equal("Failed", failed.Backups.State);
        Assert.False(failed.Backups.Latest!.Succeeded);
        Assert.Equal("gateway-20261126T060000Z.dump.age", failed.Backups.LatestSuccess!.File);
        Assert.Equal([false, true], failed.Backups.Recent.Select(run => run.Succeeded));
        Assert.Contains(failed.Alerts.Open, open => open.Severity == "Urgent" && open.Text.StartsWith("The database backup at ", StringComparison.Ordinal));

        await backups.RecordAsync(new BackupRun(clock.GetUtcNow(), true, "gateway-20261126T061000Z.dump.age", null, 90_000), None);
        Assert.Equal("Ok", (await GetAsync(reviewer)).Backups.State);

        // A day and a bit later with no further backup: overdue, and urgent.
        clock.Advance(TimeSpan.FromHours(27));
        using var nextMorning = await host.SignInReviewerAsync();
        var stale = await GetAsync(nextMorning);
        Assert.Equal("Stale", stale.Backups.State);
        Assert.Contains(stale.Alerts.Open, open => open.Severity == "Urgent" && open.Text == "No database backup has succeeded for 27 hours.");
    }

    [Fact]
    public async Task APaymentStuckBetweenTheNetworkAndItsOrder_IsUrgent_InTheConsole_AndInTheEmail()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 28, 6, 0, 0, TimeSpan.Zero));
        var sender = new RecordingSender();
        using var host = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:Fiat:Provider", "Processor");
            builder.UseSetting("Gateway:Alerts:To", "ops@example.com, second@example.com");
            builder.UseSetting("Gateway:Alerts:From", "gateway@example.com");
            builder.UseSetting("Gateway:Alerts:Smtp:Host", "127.0.0.1");
            builder.UseSetting("Gateway:Alerts:Smtp:Security", "None");
            builder.UseSetting("Gateway:Alerts:StuckPaymentAfterMinutes", "20");
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<TimeProvider>(clock);
                services.AddSingleton<IAlertSender>(sender);
                services.Remove(services.Single(service => service.ImplementationType == typeof(AlertWorker)));
            });
        });
        using var merchant = host.CreateClient();
        merchant.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GatewayApiFactory.ClientAKey);
        using var reviewer = await host.SignInReviewerAsync();
        var created = await ApiFlows.CreateOrderAsync(merchant);
        var order = (await host.Services.GetRequiredService<IOrderStore>().FindAsync(created.OrderId, None))!;

        // The network approved a payment and the gateway stopped before capturing it; the network has no record of it.
        var attempts = host.Services.GetRequiredService<IPaymentAttemptStore>();
        var attempt = PaymentAttempt.Start(
            Guid.NewGuid(), order.OrderId, order.ClientId, order.Quote.SourceAmount,
            new PaymentInstrument(PaymentMethod.Upi, SandboxTestInstruments.SuccessfulUpiAddress), clock.GetUtcNow(), TimeSpan.FromMinutes(10));
        await attempts.AddAsync(attempt, None);
        attempt.Authorize("upi_unknown", clock.GetUtcNow());
        await attempts.SaveAsync(attempt, None);

        Assert.Empty((await GetAsync(reviewer)).Alerts.Open); // not stuck yet

        clock.Advance(TimeSpan.FromMinutes(20));
        var shortOrder = order.OrderId.ToString("N")[..8].ToUpperInvariant();
        var problem = Assert.Single((await GetAsync(reviewer)).Alerts.Open);
        Assert.Equal(
            ("Urgent",
             $"A payment for order {shortOrder} (client-a) was approved 20 minutes ago and could not be captured. The gateway keeps trying, and gives the approval up after 24 hours."),
            (problem.Severity, problem.Text));

        await host.Services.GetRequiredService<AlertService>().RunAsync(None);
        var mail = Assert.Single(sender.Sent);
        Assert.Equal(["ops@example.com", "second@example.com"], mail.To);
        Assert.Equal("[Gateway Sandbox] URGENT: 1 new problem needs a person", mail.Subject);
        Assert.Contains($"order {shortOrder} (client-a) was approved 20 minutes ago", mail.Body, StringComparison.Ordinal);
        var emailed = Assert.Single((await GetAsync(reviewer)).Alerts.RecentEmails);
        Assert.Equal(("Problems", 1, "1 stuck payment"), (emailed.Kind, emailed.Problems, emailed.Summary));
    }

    [Fact]
    public async Task WithoutABackupContainerOrAlertEmail_TheStatusSaysSo()
    {
        using var reviewer = await factory.SignInReviewerAsync();

        var status = await GetAsync(reviewer);

        Assert.Equal(("NotSetUp", false), (status.Backups.State, status.Backups.Watched));
        Assert.False(status.Alerts.EmailOn);
        Assert.Equal(0, status.Alerts.Recipients);
    }

    [Fact]
    public async Task OnlyReviewersMaySee_TheSystemStatus()
    {
        using var merchant = factory.CreateClientFor(GatewayApiFactory.ClientAKey);
        using var anonymous = factory.CreateClient();

        using var asMerchant = await merchant.GetAsync(SystemPath);
        using var asAnonymous = await anonymous.GetAsync(SystemPath);

        Assert.Equal(HttpStatusCode.Forbidden, asMerchant.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, asAnonymous.StatusCode);
    }

    private static async Task<SystemStatusResponse> GetAsync(HttpClient reviewer)
    {
        using var response = await reviewer.GetAsync(SystemPath);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<SystemStatusResponse>())!;
    }

    private sealed class CapturingSender : IAlertSender
    {
        public Task SendAsync(AlertMessage message, CancellationToken cancellationToken) => Task.CompletedTask;
    }

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
