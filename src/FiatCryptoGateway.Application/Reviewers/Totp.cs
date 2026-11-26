using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace FiatCryptoGateway.Application.Reviewers;

/// <summary>
/// Time-based one-time codes (RFC 6238) exactly as authenticator apps make them by default: HMAC-SHA1 over the number
/// of 30-second steps since 1970, shortened to 6 digits.
/// </summary>
public static class Totp
{
    public const int Digits = 6;
    public const int StepSeconds = 30;

    /// <summary>160 bits, the key length RFC 4226 recommends.</summary>
    public const int SecretBytes = 20;

    public static long StepAt(DateTimeOffset at) => at.ToUnixTimeSeconds() / StepSeconds;

    public static string Code(ReadOnlySpan<byte> secret, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);
        Span<byte> mac = stackalloc byte[HMACSHA1.HashSizeInBytes];
#pragma warning disable CA5350 // RFC 6238 as authenticator apps implement it; HMAC-SHA1 is still a sound MAC.
        HMACSHA1.HashData(secret, counter, mac);
#pragma warning restore CA5350
        var offset = mac[^1] & 0x0F;
        var value = ((mac[offset] & 0x7F) << 24) | (mac[offset + 1] << 16) | (mac[offset + 2] << 8) | mac[offset + 3];
        return (value % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The step whose code matches <paramref name="code"/>, or null. The steps either side of now are accepted too,
    /// for a phone whose clock is slightly off, but never a step at or before <paramref name="lastUsedStep"/>: each
    /// code works once.
    /// </summary>
    public static long? Match(ReadOnlySpan<byte> secret, string? code, DateTimeOffset now, long lastUsedStep)
    {
        var typed = code?.Replace(" ", string.Empty, StringComparison.Ordinal);
        if (typed is not { Length: Digits } || !typed.All(char.IsAsciiDigit))
        {
            return null;
        }

        var presented = Encoding.ASCII.GetBytes(typed);
        long? matched = null;
        var current = StepAt(now);
        for (var step = current - 1; step <= current + 1; step++)
        {
            var expected = Encoding.ASCII.GetBytes(Code(secret, step));
            if (CryptographicOperations.FixedTimeEquals(expected, presented) && step > lastUsedStep)
            {
                matched ??= step;
            }
        }

        return matched;
    }

    /// <summary>A new random key, in base32 for the authenticator app.</summary>
    public static string NewSecret() => Base32.Encode(RandomNumberGenerator.GetBytes(SecretBytes));

    /// <summary>The <c>otpauth://</c> link an authenticator app reads from a QR code.</summary>
    public static string SetupLink(string issuer, string account, string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        var label = Uri.EscapeDataString(issuer) + ":" + Uri.EscapeDataString(account);
        return $"otpauth://totp/{label}?secret={secret}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits={Digits}&period={StepSeconds}";
    }
}
