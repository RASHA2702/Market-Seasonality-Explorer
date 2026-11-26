using System.Diagnostics.CodeAnalysis;
using FiatCryptoGateway.Application.Alerts;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace FiatCryptoGateway.Api.Alerts;

/// <summary>Sends alert emails through the configured mail server, as plain text.</summary>
public sealed class SmtpAlertSender(IOptions<AlertOptions> options) : IAlertSender
{
    public async Task SendAsync(AlertMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var smtp = options.Value.Smtp;
        using var mail = new MimeMessage();
        mail.From.Add(MailboxAddress.Parse(message.From));
        foreach (var recipient in message.To)
        {
            mail.To.Add(MailboxAddress.Parse(recipient));
        }

        mail.Subject = message.Subject;
        mail.Body = new TextPart(MimeKit.Text.TextFormat.Plain) { Text = message.Body };

        using var client = new MailKit.Net.Smtp.SmtpClient { Timeout = 30_000 };
        var security = smtp.Security switch
        {
            "SslOnConnect" => SecureSocketOptions.SslOnConnect,
            "None" => SecureSocketOptions.None,
            _ => SecureSocketOptions.StartTls,
        };
        await client.ConnectAsync(smtp.Host, smtp.Port, security, cancellationToken);
        if (!string.IsNullOrEmpty(smtp.Username))
        {
            // The settings check makes a username come with a password.
            await client.AuthenticateAsync(smtp.Username, smtp.Password ?? string.Empty, cancellationToken);
        }

        await client.SendAsync(mail, cancellationToken);
        await client.DisconnectAsync(quit: true, cancellationToken);
    }
}

/// <summary>Runs the alert check every <see cref="AlertOptions.CheckIntervalMinutes"/>, starting at once. Does nothing while alerts are off.</summary>
public sealed partial class AlertWorker(
    AlertService alerts,
    IOptions<AlertOptions> options,
    TimeProvider time,
    ILogger<AlertWorker> logger) : BackgroundService
{
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "A failed check or send is logged and retried on the next tick; the worker must keep running.")]
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.IsOn)
        {
            LogOff(logger);
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(settings.CheckIntervalMinutes), time);
        do
        {
            try
            {
                await alerts.RunAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogCheckFailed(logger, exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Alert emails are off: set Gateway:Alerts:To (README, Email alerts).")]
    private static partial void LogOff(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "An alert check or email failed; retrying on the next tick.")]
    private static partial void LogCheckFailed(ILogger logger, Exception exception);
}
