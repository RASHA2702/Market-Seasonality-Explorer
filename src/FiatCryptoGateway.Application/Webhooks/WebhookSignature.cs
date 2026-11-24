using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace FiatCryptoGateway.Application.Webhooks;

/// <summary>
/// HMAC-SHA256 webhook signatures. Header format: <c>t=&lt;unix seconds&gt;,v1=&lt;hex&gt;[,v1=&lt;hex&gt;...]</c>.
/// The signed payload is <c>&lt;t&gt;.&lt;raw body&gt;</c>. The timestamp limits replay; several v1 values
/// allow a secret to be rotated without downtime; comparison is constant-time.
/// Real providers each have their own scheme, which their adapters verify the same way.
/// </summary>
public static class WebhookSignature
{
    private const long MaximumUnixSeconds = 253_402_300_799;

    public static string Sign(string secret, DateTimeOffset timestamp, string body)
    {
        var unixSeconds = timestamp.ToUnixTimeSeconds();
        var signature = Convert.ToHexStringLower(Compute(secret, unixSeconds, body));
        return $"t={unixSeconds.ToString(CultureInfo.InvariantCulture)},v1={signature}";
    }

    public static bool Verify(string secret, string? header, string body, DateTimeOffset now, TimeSpan tolerance)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        ArgumentNullException.ThrowIfNull(body);
        if (string.IsNullOrWhiteSpace(header))
        {
            return false;
        }

        long? unixSeconds = null;
        var signatures = new List<byte[]>();
        foreach (var part in header.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length != 2)
            {
                return false;
            }

            if (pair[0] == "t" && long.TryParse(pair[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            {
                unixSeconds = parsed;
            }
            else if (pair[0] == "v1")
            {
                if (pair[1].Length != 64 || !IsHex(pair[1]))
                {
                    return false;
                }

                signatures.Add(Convert.FromHexString(pair[1]));
            }
        }

        if (unixSeconds is not { } seconds || seconds > MaximumUnixSeconds || signatures.Count == 0)
        {
            return false;
        }

        if ((now - DateTimeOffset.FromUnixTimeSeconds(seconds)).Duration() > tolerance)
        {
            return false;
        }

        var expected = Compute(secret, seconds, body);
        var valid = false;
        foreach (var signature in signatures)
        {
            valid |= CryptographicOperations.FixedTimeEquals(signature, expected);
        }

        return valid;
    }

    private static byte[] Compute(string secret, long unixSeconds, string body) =>
        HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes($"{unixSeconds.ToString(CultureInfo.InvariantCulture)}.{body}"));

    private static bool IsHex(string value)
    {
        foreach (var character in value)
        {
            if (!char.IsAsciiHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }
}
