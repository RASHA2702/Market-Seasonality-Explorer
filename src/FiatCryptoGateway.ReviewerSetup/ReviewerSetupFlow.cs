using System.Globalization;
using System.Text;
using FiatCryptoGateway.Application.Reviewers;

namespace FiatCryptoGateway.ReviewerSetup;

/// <summary>Where the setup command reads and writes. <see cref="ReadSecret"/> must not echo what is typed.</summary>
public interface ITerminal
{
    string? ReadLine();

    string? ReadSecret();

    void Write(string text);

    void WriteLine(string text = "");
}

/// <summary>
/// Creates the settings for one reviewer account: asks for a username and password, makes a new authenticator key,
/// shows it as a QR code, checks one code from the reviewer's app, and prints the environment variables to set.
/// Runs entirely on the reviewer's computer; nothing is sent anywhere and nothing is written to disk.
/// </summary>
public sealed class ReviewerSetupFlow(ITerminal terminal, TimeProvider time, Func<string> newSecret, Func<string, string> hashPassword)
{
    public const string DefaultIssuer = "Gateway sandbox";
    private const int MaxCodeAttempts = 5;

    public ReviewerSetupFlow(ITerminal terminal)
        : this(terminal, TimeProvider.System, Totp.NewSecret, password => PasswordHashing.Hash(password))
    {
    }

    /// <summary>0 when the settings were printed; 1 if setup stopped; 2 for wrong arguments.</summary>
    public int Run(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (!TryReadArguments(args, out var index, out var issuer, out var colours))
        {
            terminal.WriteLine("Usage: dotnet run --project src/FiatCryptoGateway.ReviewerSetup [-- [--index N] [--issuer NAME] [--no-colour]]");
            terminal.WriteLine("  --index N      the reviewer's number in the settings: 0 for the first reviewer, 1 for the second, ...");
            terminal.WriteLine($"  --issuer NAME  the name the authenticator app shows (default \"{DefaultIssuer}\")");
            terminal.WriteLine("  --no-colour    draw the QR code without terminal colours");
            return 2;
        }

        terminal.WriteLine("Reviewer account setup for the gateway's reviewer console.");
        terminal.WriteLine("Run this on the reviewer's own computer. Nothing is sent anywhere or saved to disk.");
        terminal.WriteLine();

        var username = Ask($"Username ({ReviewerNames.UsernameRule})", ReviewerNames.IsValidUsername, "That is not a valid username.");
        if (username is null)
        {
            return Stopped();
        }

        var displayName = Ask(
            "Name shown in the console (press Enter to use the username)",
            name => name.Length == 0 || ReviewerNames.IsValidDisplayName(name),
            "Use 1 to 100 printable characters.");
        if (displayName is null)
        {
            return Stopped();
        }

        var password = AskPassword();
        if (password is null)
        {
            return Stopped();
        }

        var secret = newSecret();
        var key = Base32.TryDecode(secret) ?? throw new InvalidOperationException("The new authenticator key is not base32.");
        var link = Totp.SetupLink(issuer, username, secret);
        terminal.WriteLine();
        terminal.WriteLine("Add the account to an authenticator app (Google Authenticator, Microsoft Authenticator, 1Password and others)");
        terminal.WriteLine("by scanning this code:");
        terminal.WriteLine();
        foreach (var line in QrTerminal.Render(link, colours))
        {
            terminal.WriteLine("  " + line);
        }

        terminal.WriteLine();
        terminal.WriteLine($"Or enter this key by hand (time-based, 6 digits): {Grouped(secret)}");
        terminal.WriteLine($"Or paste this link into a password manager that keeps one-time codes: {link}");
        terminal.WriteLine("Keep the key private: anyone who has it can make this reviewer's codes.");
        terminal.WriteLine();

        if (!CheckCode(key))
        {
            return Stopped();
        }

        var hash = hashPassword(password);
        var prefix = string.Create(CultureInfo.InvariantCulture, $"Gateway__Review__Reviewers__{index}__");
        terminal.WriteLine();
        terminal.WriteLine("Done. Set these environment variables where the API runs (or put the same names and values in your");
        terminal.WriteLine("secret store), then restart the API. Never commit them or paste them into a chat.");
        terminal.WriteLine();
        terminal.WriteLine($"export {prefix}Username={username}");
        if (displayName.Length > 0)
        {
            terminal.WriteLine($"export {prefix}DisplayName={ShellQuote(displayName)}");
        }

        terminal.WriteLine($"export {prefix}PasswordHash={hash}");
        terminal.WriteLine($"export {prefix}TotpSecret={secret}");
        return 0;
    }

    private static string Grouped(string secret) =>
        string.Join(' ', Enumerable.Range(0, (secret.Length + 3) / 4).Select(group => secret.Substring(group * 4, Math.Min(4, secret.Length - (group * 4)))));

    private static string ShellQuote(string text) => "'" + text.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static bool TryReadArguments(IReadOnlyList<string> args, out int index, out string issuer, out bool colours)
    {
        index = 0;
        issuer = DefaultIssuer;
        colours = true;
        for (var position = 0; position < args.Count; position++)
        {
            switch (args[position])
            {
                case "--index" when position + 1 < args.Count &&
                    int.TryParse(args[position + 1], NumberStyles.None, CultureInfo.InvariantCulture, out index) && index <= 99:
                    position++;
                    break;
                case "--issuer" when position + 1 < args.Count && ReviewerNames.IsValidDisplayName(args[position + 1]):
                    issuer = args[++position];
                    break;
                case "--no-colour" or "--no-color":
                    colours = false;
                    break;
                default:
                    return false;
            }
        }

        return true;
    }

    private string? Ask(string question, Func<string, bool> isValid, string problem)
    {
        while (true)
        {
            terminal.Write(question + ": ");
            var answer = terminal.ReadLine()?.Trim();
            if (answer is null || isValid(answer))
            {
                return answer;
            }

            terminal.WriteLine(problem);
        }
    }

    private string? AskPassword()
    {
        while (true)
        {
            terminal.Write($"Password ({PasswordHashing.MinimumPasswordLength} to {PasswordHashing.MaximumPasswordLength} characters; a phrase of several words is easiest): ");
            var password = terminal.ReadSecret();
            terminal.WriteLine();
            if (password is null)
            {
                return null;
            }

            if (!PasswordHashing.IsAllowedPassword(password))
            {
                terminal.WriteLine($"Use {PasswordHashing.MinimumPasswordLength} to {PasswordHashing.MaximumPasswordLength} characters.");
                continue;
            }

            terminal.Write("Type it again: ");
            var repeated = terminal.ReadSecret();
            terminal.WriteLine();
            if (repeated is null)
            {
                return null;
            }

            if (string.Equals(password, repeated, StringComparison.Ordinal))
            {
                return password;
            }

            terminal.WriteLine("The two passwords differ. Try again.");
        }
    }

    private bool CheckCode(byte[] key)
    {
        for (var attempt = 1; attempt <= MaxCodeAttempts; attempt++)
        {
            terminal.Write("Enter the 6-digit code the app shows now: ");
            var code = terminal.ReadLine();
            if (code is null)
            {
                return false;
            }

            if (Totp.Match(key, code, time.GetUtcNow(), lastUsedStep: 0) is not null)
            {
                terminal.WriteLine("The code matches.");
                return true;
            }

            terminal.WriteLine("That code does not match. Check that the phone's clock is set automatically, then try the next code.");
        }

        terminal.WriteLine($"No matching code after {MaxCodeAttempts} tries.");
        return false;
    }

    private int Stopped()
    {
        terminal.WriteLine();
        terminal.WriteLine("Setup stopped. Nothing was created.");
        return 1;
    }
}

/// <summary>The real console. Passwords are read without echo when a person is typing.</summary>
public sealed class ConsoleTerminal : ITerminal
{
    public string? ReadLine() => Console.ReadLine();

    public string? ReadSecret()
    {
        if (Console.IsInputRedirected)
        {
            return Console.ReadLine();
        }

        var secret = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    return secret.ToString();
                case ConsoleKey.Backspace:
                    if (secret.Length > 0)
                    {
                        secret.Length--;
                    }

                    break;
                default:
                    if (!char.IsControl(key.KeyChar))
                    {
                        secret.Append(key.KeyChar);
                    }

                    break;
            }
        }
    }

    public void Write(string text) => Console.Write(text);

    public void WriteLine(string text = "") => Console.WriteLine(text);
}
