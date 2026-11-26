using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;

namespace FiatCryptoGateway.Application.Reviewers;

/// <summary>
/// Reviewer password hashes: PBKDF2 with HMAC-SHA256, a random 16-byte salt and 600,000 iterations (the OWASP
/// recommendation for this algorithm). The text form is <c>pbkdf2-sha256:&lt;iterations&gt;:&lt;salt&gt;:&lt;hash&gt;</c> with
/// base64url parts, so it needs no quoting in a shell or an environment file. The password itself is never stored.
/// </summary>
public static class PasswordHashing
{
    public const int Iterations = 600_000;
    public const int MinimumPasswordLength = 12;
    public const int MaximumPasswordLength = 128;

    private const string Scheme = "pbkdf2-sha256";
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const int MaximumIterations = 10_000_000;

    /// <summary>
    /// Hashes <paramref name="password"/> with a new salt. Fewer iterations are only for tests: the gateway refuses
    /// a hash with fewer than <see cref="Iterations"/> at startup.
    /// </summary>
    public static string Hash(string password, int iterations = Iterations)
    {
        ArgumentNullException.ThrowIfNull(password);
        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(iterations, MaximumIterations);
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, HashBytes);
        return string.Join(
            ':', Scheme, iterations.ToString(CultureInfo.InvariantCulture), Base64Url.EncodeToString(salt), Base64Url.EncodeToString(hash));
    }

    /// <summary>Whether <paramref name="password"/> matches. A malformed hash matches nothing.</summary>
    public static bool Verify(string password, string? encoded)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (!TryParse(encoded, out var iterations, out var salt, out var expected))
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>Well formed and with at least <see cref="Iterations"/> iterations: what the gateway accepts at startup.</summary>
    public static bool IsAcceptable(string? encoded) => TryParse(encoded, out var iterations, out _, out _) && iterations >= Iterations;

    /// <summary>The password rules shown to whoever sets one: length only, as current guidance (NIST SP 800-63B) advises.</summary>
    public static bool IsAllowedPassword(string? password) =>
        password is { Length: >= MinimumPasswordLength and <= MaximumPasswordLength } && !string.IsNullOrWhiteSpace(password);

    private static bool TryParse(string? encoded, out int iterations, out byte[] salt, out byte[] hash)
    {
        iterations = 0;
        salt = [];
        hash = [];
        var parts = encoded?.Split(':');
        if (parts is not [Scheme, var count, var saltText, var hashText] ||
            !int.TryParse(count, NumberStyles.None, CultureInfo.InvariantCulture, out iterations) ||
            iterations is < 1 or > MaximumIterations)
        {
            return false;
        }

        try
        {
            salt = Base64Url.DecodeFromChars(saltText);
            hash = Base64Url.DecodeFromChars(hashText);
        }
        catch (FormatException)
        {
            return false;
        }

        return salt.Length == SaltBytes && hash.Length == HashBytes;
    }
}
