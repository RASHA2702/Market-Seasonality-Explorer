using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace FiatCryptoGateway.Application.Reviewers;

/// <summary>
/// One person who may use the reviewer console. Create the values with the reviewer setup command
/// (<c>dotnet run --project src/FiatCryptoGateway.ReviewerSetup</c>) and supply them through environment variables or a
/// secret store, never a committed file: the authenticator key lets anyone make this reviewer's codes.
/// </summary>
public sealed class ReviewerAccount
{
    /// <summary>Recorded with every decision this reviewer makes.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Shown in the console. Defaults to the username.</summary>
    public string? DisplayName { get; set; }

    /// <summary>PBKDF2-SHA256 hash of the password (see <see cref="PasswordHashing"/>). The password itself is never stored.</summary>
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>The base32 key the reviewer's authenticator app holds.</summary>
    public string TotpSecret { get; set; } = string.Empty;
}

/// <summary>Who may review, and how sign-in and sessions behave. Section <c>Gateway:Review</c>.</summary>
public sealed class ReviewerAccessOptions
{
    public const string SectionName = "Gateway:Review";

    public IList<ReviewerAccount> Reviewers { get; } = new List<ReviewerAccount>();

    /// <summary>A session ends after this long without a request.</summary>
    public int SessionIdleMinutes { get; set; } = 30;

    /// <summary>A session ends this long after sign-in, however active.</summary>
    public int SessionMaxHours { get; set; } = 12;

    /// <summary>Failed sign-ins in a row before sign-in pauses for <see cref="LockoutMinutes"/>.</summary>
    public int MaxFailedAttempts { get; set; } = 5;

    public int LockoutMinutes { get; set; } = 15;

    /// <summary>Sign-in requests allowed per network address per minute, for all usernames together.</summary>
    public int SignInAttemptsPerMinute { get; set; } = 10;

    public ReviewerAccount? Find(string username) =>
        Reviewers.FirstOrDefault(reviewer => string.Equals(reviewer.Username, username, StringComparison.Ordinal));
}

public static partial class ReviewerNames
{
    public const string UsernameRule = "2 to 32 characters: lowercase letters, digits, '.', '_' or '-', starting with a letter";

    public static bool IsValidUsername(string? username) => username is not null && UsernamePattern().IsMatch(username);

    public static bool IsValidDisplayName(string? name) =>
        name is { Length: >= 1 and <= 100 } && !string.IsNullOrWhiteSpace(name) && !name.Any(char.IsControl);

    public static string DisplayNameOf(ReviewerAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        return string.IsNullOrWhiteSpace(account.DisplayName) ? account.Username : account.DisplayName;
    }

    [GeneratedRegex(@"^[a-z][a-z0-9._-]{1,31}\z", RegexOptions.CultureInvariant)]
    private static partial Regex UsernamePattern();
}

public sealed class ReviewerAccessOptionsValidator : IValidateOptions<ReviewerAccessOptions>
{
    public const int MinimumSecretBytes = 20;
    public const int MaximumSecretBytes = 64;

    public ValidateOptionsResult Validate(string? name, ReviewerAccessOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        for (var index = 0; index < options.Reviewers.Count; index++)
        {
            var reviewer = options.Reviewers[index];

            // Messages name the reviewer and the rule, never a hash or key.
            var who = ReviewerNames.IsValidUsername(reviewer.Username) ? $"'{reviewer.Username}'" : $"number {index}";
            if (!ReviewerNames.IsValidUsername(reviewer.Username))
            {
                failures.Add($"Reviewer {who} needs a Username of {ReviewerNames.UsernameRule}.");
            }

            if (reviewer.DisplayName is not null && !ReviewerNames.IsValidDisplayName(reviewer.DisplayName))
            {
                failures.Add($"Reviewer {who} needs a DisplayName of 1 to 100 printable characters, or none.");
            }

            if (!PasswordHashing.IsAcceptable(reviewer.PasswordHash))
            {
                failures.Add(
                    $"Reviewer {who} needs PasswordHash from the reviewer setup command " +
                    $"(PBKDF2-SHA256 with at least {PasswordHashing.Iterations:N0} iterations).");
            }

            if (Base32.TryDecode(reviewer.TotpSecret) is not { Length: >= MinimumSecretBytes and <= MaximumSecretBytes })
            {
                failures.Add($"Reviewer {who} needs TotpSecret: the authenticator key from the reviewer setup command (base32, at least 160 bits).");
            }
        }

        if (options.Reviewers.Select(reviewer => reviewer.Username).Distinct(StringComparer.Ordinal).Count() != options.Reviewers.Count)
        {
            failures.Add("Reviewer usernames must be unique.");
        }

        if (options.SessionIdleMinutes is < 1 or > 240)
        {
            failures.Add("Review SessionIdleMinutes must be between 1 and 240.");
        }

        if (options.SessionMaxHours is < 1 or > 24 || options.SessionMaxHours * 60 < options.SessionIdleMinutes)
        {
            failures.Add("Review SessionMaxHours must be between 1 and 24, and at least as long as SessionIdleMinutes.");
        }

        if (options.MaxFailedAttempts is < 1 or > 100 || options.LockoutMinutes is < 1 or > 1440)
        {
            failures.Add("Review MaxFailedAttempts must be between 1 and 100, and LockoutMinutes between 1 and 1440.");
        }

        if (options.SignInAttemptsPerMinute is < 1 or > 1000)
        {
            failures.Add("Review SignInAttemptsPerMinute must be between 1 and 1000.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
