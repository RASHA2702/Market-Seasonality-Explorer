using System.Net.Mail;
using Microsoft.Extensions.Options;

namespace FiatCryptoGateway.Application.Alerts;

/// <summary>Who is emailed about problems, how, and when. Section <c>Gateway:Alerts</c>. Off until <see cref="To"/> is set.</summary>
public sealed class AlertOptions
{
    public const string SectionName = "Gateway:Alerts";

    /// <summary>Comma-separated email addresses. Empty: no alerts are sent.</summary>
    public string To { get; set; } = string.Empty;

    public string From { get; set; } = string.Empty;

    public AlertSmtpOptions Smtp { get; set; } = new();

    public int CheckIntervalMinutes { get; set; } = 5;

    /// <summary>A held payment or a wallet waiting for review is reported once it has waited this long, and again when the review limit passes.</summary>
    public int ReviewWarnAfterHours { get; set; } = 18;

    /// <summary>While problems stay open, they are emailed again after this long.</summary>
    public int RemindAfterHours { get; set; } = 12;

    /// <summary>Report when no backup has succeeded for this long. 0: backups are not watched (no backup container).</summary>
    public int BackupMaxAgeHours { get; set; }

    /// <summary>The reviewer console's address, linked from every email.</summary>
    public Uri? ConsoleUrl { get; set; }

    public IReadOnlyList<string> Recipients => To.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public bool IsOn => Recipients.Count > 0;
}

/// <summary>The mail server alerts are sent through. Keep <see cref="Password"/> in an environment variable or secret store.</summary>
public sealed class AlertSmtpOptions
{
    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 587;

    public string? Username { get; set; }

    public string? Password { get; set; }

    /// <summary><c>StartTls</c> (default, port 587), <c>SslOnConnect</c> (port 465), or <c>None</c> for a mail server on this machine only.</summary>
    public string Security { get; set; } = "StartTls";
}

public sealed class AlertOptionsValidator : IValidateOptions<AlertOptions>
{
    private static readonly string[] SecurityModes = ["StartTls", "SslOnConnect", "None"];

    public ValidateOptionsResult Validate(string? name, AlertOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        if (options.CheckIntervalMinutes is < 1 or > 60 || options.RemindAfterHours is < 1 or > 168 ||
            options.ReviewWarnAfterHours is < 0 or > 168 || options.BackupMaxAgeHours is < 0 or > 168)
        {
            failures.Add("Alerts CheckIntervalMinutes must be 1 to 60; RemindAfterHours 1 to 168; ReviewWarnAfterHours and BackupMaxAgeHours 0 to 168.");
        }

        if (options.ConsoleUrl is { } console && (!console.IsAbsoluteUri || console.Scheme is not ("https" or "http")))
        {
            failures.Add("Alerts ConsoleUrl must be an absolute web address, such as https://pay.example.com/review.");
        }

        if (!options.IsOn)
        {
            return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
        }

        // Messages name the setting, never the password.
        if (options.Recipients.Any(address => !MailAddress.TryCreate(address, out _)))
        {
            failures.Add("Alerts To must be email addresses separated by commas.");
        }

        if (!MailAddress.TryCreate(options.From, out _))
        {
            failures.Add("Alerts From must be the email address alerts are sent from.");
        }

        var smtp = options.Smtp;
        if (string.IsNullOrWhiteSpace(smtp.Host) || smtp.Port is < 1 or > 65535)
        {
            failures.Add("Alerts Smtp:Host and Smtp:Port must name the mail server alerts are sent through.");
        }

        if (!SecurityModes.Contains(smtp.Security, StringComparer.Ordinal))
        {
            failures.Add("Alerts Smtp:Security must be StartTls, SslOnConnect or None.");
        }
        else if (smtp.Security == "None" && smtp.Host is not ("localhost" or "127.0.0.1" or "::1"))
        {
            failures.Add("Alerts Smtp:Security None sends the email and password unencrypted; use it only for a mail server on this machine.");
        }

        if (string.IsNullOrEmpty(smtp.Username) != string.IsNullOrEmpty(smtp.Password))
        {
            failures.Add("Alerts Smtp:Username and Smtp:Password go together: set both, or neither.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
