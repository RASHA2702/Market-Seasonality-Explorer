using System.Text;
using FiatCryptoGateway.Application.Reviewers;
using FiatCryptoGateway.ReviewerSetup;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FiatCryptoGateway.Tests;

public sealed class ReviewerSetupTests
{
    private const string Password = "correct horse battery staple";

    // Made at run time, so no key-like text sits in the repository.
    private static readonly string Secret = Base32.Encode([.. Enumerable.Range(1, Totp.SecretBytes).Select(value => (byte)value)]);

    private readonly ManualTimeProvider _time = new(Fixtures.Start);

    [Fact]
    public void Setup_PrintsSettingsTheGatewayAccepts()
    {
        var terminal = new ScriptedTerminal("compliance-officer", "Ops team's desk", Password, Password, CodeNow());

        var exit = Flow(terminal).Run([]);

        Assert.Equal(0, exit);
        var settings = Exports(terminal.Output);
        Assert.Equal("compliance-officer", settings["Gateway:Review:Reviewers:0:Username"]);
        Assert.Equal("Ops team's desk", settings["Gateway:Review:Reviewers:0:DisplayName"]);
        Assert.Equal(Secret, settings["Gateway:Review:Reviewers:0:TotpSecret"]);
        Assert.True(PasswordHashing.Verify(Password, settings["Gateway:Review:Reviewers:0:PasswordHash"]));

        var options = Bind(settings);
        Assert.True(new ReviewerAccessOptionsValidator().Validate(null, options).Succeeded);
        Assert.Contains(Totp.SetupLink(ReviewerSetupFlow.DefaultIssuer, "compliance-officer", Secret), terminal.Output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Password, terminal.Output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Setup_AsksAgainUntilEachAnswerIsUsable()
    {
        var terminal = new ScriptedTerminal(
            "Reviewer", "r", "reviewer", string.Empty,
            "too short", "a long enough password", "a different password", Password, Password,
            "000000", "12345", CodeNow());

        var exit = Flow(terminal).Run(["--index", "2", "--issuer", "Acme Payments"]);

        Assert.Equal(0, exit);
        var output = terminal.Output.ToString();
        Assert.Equal(2, Count(output, "That is not a valid username."));
        Assert.Equal(1, Count(output, "Use 12 to 128 characters."));
        Assert.Equal(1, Count(output, "The two passwords differ."));
        Assert.Equal(2, Count(output, "That code does not match."));
        var settings = Exports(terminal.Output);
        Assert.Equal("reviewer", settings["Gateway:Review:Reviewers:2:Username"]);
        Assert.False(settings.ContainsKey("Gateway:Review:Reviewers:2:DisplayName"));
        Assert.Contains("otpauth://totp/Acme%20Payments:reviewer?", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Setup_WithoutAMatchingCode_CreatesNothing()
    {
        var terminal = new ScriptedTerminal("reviewer", string.Empty, Password, Password, "000000", "111111", "222222", "333333", "444444");

        var exit = Flow(terminal).Run([]);

        Assert.Equal(1, exit);
        Assert.DoesNotContain("PasswordHash", terminal.Output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Setup stopped. Nothing was created.", terminal.Output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Setup_StopsWhenInputEnds()
    {
        var terminal = new ScriptedTerminal("reviewer", string.Empty, Password);

        Assert.Equal(1, Flow(terminal).Run([]));
        Assert.DoesNotContain("export", terminal.Output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--index")]
    [InlineData("--index", "-1")]
    [InlineData("--index", "100")]
    [InlineData("--issuer")]
    [InlineData("--verbose")]
    public void Setup_RefusesUnknownArguments(params string[] args)
    {
        var terminal = new ScriptedTerminal();

        Assert.Equal(2, Flow(terminal).Run(args));
        Assert.StartsWith("Usage:", terminal.Output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void QrCode_IsDrawnDarkOnLight_TwoModulesPerCharacter()
    {
        var lines = QrTerminal.Render("otpauth://totp/Gateway:reviewer?secret=" + Secret, colours: false);

        var width = lines[0].Length;
        Assert.All(lines, line => Assert.Equal(width, line.Length));
        Assert.Equal((width + 1) / 2, lines.Count);
        Assert.Equal(new string(' ', width), lines[0]); // quiet zone

        // The top-left finder pattern: a full row of 7 dark modules over a row dark only at both ends.
        var finder = lines[QrTerminal.Border / 2].Substring(QrTerminal.Border, 7);
        Assert.Equal("█▀▀▀▀▀█", finder);
        Assert.StartsWith("\u001b[30;47m", QrTerminal.Render("x")[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Options_RefuseWeakOrMissingCredentials()
    {
        var valid = new ReviewerAccount
        {
            Username = "compliance-officer",
            PasswordHash = PasswordHashing.Hash(Password),
            TotpSecret = Secret,
        };
        Assert.True(Validate(valid).Succeeded);

        Assert.Contains("Username", Validate(Copy(valid, account => account.Username = "Compliance Officer")).FailureMessage, StringComparison.Ordinal);
        Assert.Contains("Username", Validate(Copy(valid, account => account.Username = "reviewer\n")).FailureMessage, StringComparison.Ordinal);
        Assert.Contains("DisplayName", Validate(Copy(valid, account => account.DisplayName = " ")).FailureMessage, StringComparison.Ordinal);
        Assert.Contains("PasswordHash", Validate(Copy(valid, account => account.PasswordHash = PasswordHashing.Hash(Password, iterations: 100_000))).FailureMessage, StringComparison.Ordinal);
        Assert.Contains("PasswordHash", Validate(Copy(valid, account => account.PasswordHash = Password)).FailureMessage, StringComparison.Ordinal);
        Assert.Contains("TotpSecret", Validate(Copy(valid, account => account.TotpSecret = Secret[..24])).FailureMessage, StringComparison.Ordinal);
        Assert.Contains("TotpSecret", Validate(Copy(valid, account => account.TotpSecret = string.Empty)).FailureMessage, StringComparison.Ordinal);

        var twice = new ReviewerAccessOptions();
        twice.Reviewers.Add(valid);
        twice.Reviewers.Add(Copy(valid, _ => { }));
        Assert.Contains("unique", new ReviewerAccessOptionsValidator().Validate(null, twice).FailureMessage, StringComparison.Ordinal);

        // Failure messages never repeat a hash or a key.
        var broken = Validate(Copy(valid, account => account.TotpSecret = "0" + Secret)).FailureMessage;
        Assert.DoesNotContain(Secret, broken, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SessionIdleMinutes", "0")]
    [InlineData("SessionMaxHours", "25")]
    [InlineData("MaxFailedAttempts", "0")]
    [InlineData("LockoutMinutes", "0")]
    [InlineData("SignInAttemptsPerMinute", "0")]
    public void Options_RefuseSessionAndLockoutSettingsOutOfRange(string setting, string value)
    {
        var options = Bind(new Dictionary<string, string?> { [$"Gateway:Review:{setting}"] = value });

        Assert.False(new ReviewerAccessOptionsValidator().Validate(null, options).Succeeded);
    }

    private static ValidateOptionsResult Validate(ReviewerAccount account)
    {
        var options = new ReviewerAccessOptions();
        options.Reviewers.Add(account);
        return new ReviewerAccessOptionsValidator().Validate(null, options);
    }

    private static ReviewerAccount Copy(ReviewerAccount account, Action<ReviewerAccount> change)
    {
        var copy = new ReviewerAccount
        {
            Username = account.Username,
            DisplayName = account.DisplayName,
            PasswordHash = account.PasswordHash,
            TotpSecret = account.TotpSecret,
        };
        change(copy);
        return copy;
    }

    private static ReviewerAccessOptions Bind(IDictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddOptions<ReviewerAccessOptions>().Bind(configuration.GetSection(ReviewerAccessOptions.SectionName));
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<ReviewerAccessOptions>>().Value;
    }

    /// <summary>The printed <c>export</c> lines as configuration keys, the way environment variables are read.</summary>
    private static Dictionary<string, string?> Exports(StringBuilder output)
    {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var line in output.ToString().Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.StartsWith("export ", StringComparison.Ordinal)))
        {
            var (name, value) = (line["export ".Length..line.IndexOf('=', StringComparison.Ordinal)], line[(line.IndexOf('=', StringComparison.Ordinal) + 1)..]);
            if (value.StartsWith('\''))
            {
                value = value[1..^1].Replace("'\\''", "'", StringComparison.Ordinal);
            }

            settings[name.Replace("__", ":", StringComparison.Ordinal)] = value;
        }

        return settings;
    }

    private static int Count(string text, string part) => (text.Length - text.Replace(part, string.Empty, StringComparison.Ordinal).Length) / part.Length;

    private string CodeNow() => Totp.Code(Base32.TryDecode(Secret), Totp.StepAt(_time.GetUtcNow()));

    private ReviewerSetupFlow Flow(ITerminal terminal) => new(terminal, _time, () => Secret, password => PasswordHashing.Hash(password));

    private sealed class ScriptedTerminal(params string[] input) : ITerminal
    {
        private readonly Queue<string> _input = new(input);

        public StringBuilder Output { get; } = new();

        public string? ReadLine() => _input.TryDequeue(out var line) ? line : null;

        public string? ReadSecret() => ReadLine();

        public void Write(string text) => Output.Append(text);

        public void WriteLine(string text = "") => Output.AppendLine(text);
    }
}
