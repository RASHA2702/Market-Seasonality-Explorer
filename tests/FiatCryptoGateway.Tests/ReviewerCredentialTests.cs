using System.Text;
using System.Text.RegularExpressions;
using FiatCryptoGateway.Application.Reviewers;

namespace FiatCryptoGateway.Tests;

public sealed partial class ReviewerCredentialTests
{
    // RFC 6238 appendix B: the SHA-1 test key, and the last six digits of each eight-digit code.
    private static readonly byte[] RfcSecret = Encoding.ASCII.GetBytes("12345678901234567890");

    [Theory]
    [InlineData("", "")]
    [InlineData("f", "MY")]
    [InlineData("fo", "MZXQ")]
    [InlineData("foo", "MZXW6")]
    [InlineData("foob", "MZXW6YQ")]
    [InlineData("fooba", "MZXW6YTB")]
    [InlineData("foobar", "MZXW6YTBOI")]
    public void Base32_MatchesRfc4648(string text, string encoded)
    {
        Assert.Equal(encoded, Base32.Encode(Encoding.ASCII.GetBytes(text)));
        Assert.Equal(text, Encoding.ASCII.GetString(Base32.TryDecode(encoded)!));
    }

    [Fact]
    public void Base32_ReadsKeysAsPeopleTypeThem_AndRefusesAnythingElse()
    {
        Assert.Equal("foobar", Encoding.ASCII.GetString(Base32.TryDecode("mzxw 6ytb oi======")!));
        Assert.Null(Base32.TryDecode("MZXW6YTB0I")); // a zero is not base32
        Assert.Null(Base32.TryDecode("MZXW-6YTB"));
        Assert.Null(Base32.TryDecode(null));
        var secret = Totp.NewSecret();
        Assert.Equal(32, secret.Length);
        Assert.Equal(Totp.SecretBytes, Base32.TryDecode(secret)!.Length);
    }

    [Theory]
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1111111111L, "050471")]
    [InlineData(1234567890L, "005924")]
    [InlineData(2000000000L, "279037")]
    [InlineData(20000000000L, "353130")]
    public void Totp_MatchesRfc6238(long unixSeconds, string code)
    {
        var at = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        Assert.Equal(code, Totp.Code(RfcSecret, Totp.StepAt(at)));
    }

    [Fact]
    public void Totp_AcceptsOneStepEitherSide_OnlyOnce()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1111111111);
        var step = Totp.StepAt(now);

        Assert.Equal(step, Totp.Match(RfcSecret, Totp.Code(RfcSecret, step), now, lastUsedStep: 0));
        Assert.Equal(step - 1, Totp.Match(RfcSecret, Totp.Code(RfcSecret, step - 1), now, lastUsedStep: 0));
        Assert.Equal(step + 1, Totp.Match(RfcSecret, Totp.Code(RfcSecret, step + 1), now, lastUsedStep: 0));
        Assert.Null(Totp.Match(RfcSecret, Totp.Code(RfcSecret, step - 2), now, lastUsedStep: 0));
        Assert.Null(Totp.Match(RfcSecret, Totp.Code(RfcSecret, step + 2), now, lastUsedStep: 0));

        // Used once, a code (or an earlier one) never works again.
        Assert.Null(Totp.Match(RfcSecret, Totp.Code(RfcSecret, step), now, lastUsedStep: step));
        Assert.Null(Totp.Match(RfcSecret, Totp.Code(RfcSecret, step - 1), now, lastUsedStep: step));
        Assert.Equal(step + 1, Totp.Match(RfcSecret, Totp.Code(RfcSecret, step + 1), now, lastUsedStep: step));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("05047")]
    [InlineData("0504711")]
    [InlineData("05047a")]
    [InlineData("٠٥٠٤٧١")] // Arabic-Indic digits are digits, but not the code
    public void Totp_RefusesAnythingButSixDigits(string? code)
    {
        Assert.Null(Totp.Match(RfcSecret, code, DateTimeOffset.FromUnixTimeSeconds(1111111111), lastUsedStep: 0));
    }

    [Fact]
    public void Totp_AcceptsACodeTypedWithASpace()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1111111111);
        Assert.Equal(Totp.StepAt(now), Totp.Match(RfcSecret, "050 471", now, lastUsedStep: 0));
    }

    [Fact]
    public void SetupLink_IsWhatAuthenticatorAppsRead()
    {
        Assert.Equal(
            "otpauth://totp/Sandbox%20Gateway:compliance-officer?secret=MZXW6YTBOI&issuer=Sandbox%20Gateway&algorithm=SHA1&digits=6&period=30",
            Totp.SetupLink("Sandbox Gateway", "compliance-officer", "MZXW6YTBOI"));
    }

    [Fact]
    public void PasswordHash_VerifiesOnlyTheSamePassword()
    {
        var hash = PasswordHashing.Hash("correct horse battery staple", iterations: 1_000);

        Assert.True(PasswordHashing.Verify("correct horse battery staple", hash));
        Assert.False(PasswordHashing.Verify("correct horse battery stapler", hash));
        Assert.False(PasswordHashing.Verify("Correct horse battery staple", hash));
        Assert.NotEqual(hash, PasswordHashing.Hash("correct horse battery staple", iterations: 1_000)); // a new salt each time
        Assert.Matches(ShellSafe(), hash);
    }

    [Fact]
    public void PasswordHash_ThatWasTamperedWith_MatchesNothing()
    {
        var hash = PasswordHashing.Hash("correct horse battery staple", iterations: 1_000);
        var parts = hash.Split(':');

        Assert.False(PasswordHashing.Verify("correct horse battery staple", string.Join(':', parts[0], "1001", parts[2], parts[3])));
        Assert.False(PasswordHashing.Verify("correct horse battery staple", string.Join(':', parts[0], parts[1], parts[3], parts[2])));
        Assert.False(PasswordHashing.Verify("correct horse battery staple", string.Join(':', "pbkdf2-sha512", parts[1], parts[2], parts[3])));
        Assert.False(PasswordHashing.Verify("correct horse battery staple", hash + ":extra"));
        Assert.False(PasswordHashing.Verify("correct horse battery staple", string.Join(':', parts[0], parts[1], parts[2], "not base64!")));
        Assert.False(PasswordHashing.Verify("correct horse battery staple", null));
        Assert.False(PasswordHashing.Verify("correct horse battery staple", string.Empty));
    }

    [Fact]
    public void PasswordHash_IsAcceptedAtStartupOnlyWithFullStrength()
    {
        var full = PasswordHashing.Hash("correct horse battery staple");

        Assert.StartsWith("pbkdf2-sha256:600000:", full, StringComparison.Ordinal);
        Assert.True(PasswordHashing.IsAcceptable(full));
        Assert.True(PasswordHashing.Verify("correct horse battery staple", full));
        Assert.False(PasswordHashing.IsAcceptable(PasswordHashing.Hash("correct horse battery staple", iterations: 599_999)));
        Assert.False(PasswordHashing.IsAcceptable("correct horse battery staple"));
    }

    [Theory]
    [InlineData("short pass", false)]
    [InlineData("            ", false)]
    [InlineData("twelve chars", true)]
    [InlineData("a much longer passphrase with spaces", true)]
    public void Passwords_NeedTwelveCharacters(string password, bool allowed)
    {
        Assert.Equal(allowed, PasswordHashing.IsAllowedPassword(password));
        Assert.False(PasswordHashing.IsAllowedPassword(new string('x', PasswordHashing.MaximumPasswordLength + 1)));
    }

    [GeneratedRegex("^[A-Za-z0-9:_-]+$")]
    private static partial Regex ShellSafe();
}
