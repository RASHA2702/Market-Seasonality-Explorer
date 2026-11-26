using System.Text;

namespace FiatCryptoGateway.Application.Reviewers;

/// <summary>RFC 4648 base32: the alphabet authenticator apps use for the keys they are given.</summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>Upper case, without padding.</summary>
    public static string Encode(ReadOnlySpan<byte> data)
    {
        var text = new StringBuilder(((data.Length * 8) + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var value in data)
        {
            buffer = (buffer << 8) | value;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                text.Append(Alphabet[(buffer >> bits) & 31]);
            }

            buffer &= (1 << bits) - 1;
        }

        if (bits > 0)
        {
            text.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        }

        return text.ToString();
    }

    /// <summary>
    /// Reads base32 in either case, ignoring spaces between groups and padding at the end. Returns null for anything
    /// else, so a mistyped key is refused rather than silently changed.
    /// </summary>
    public static byte[]? TryDecode(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var bytes = new List<byte>(text.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var character in text.TrimEnd('='))
        {
            if (character == ' ')
            {
                continue;
            }

            var value = Alphabet.IndexOf(char.ToUpperInvariant(character), StringComparison.Ordinal);
            if (value < 0)
            {
                return null;
            }

            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                bytes.Add((byte)(buffer >> bits));
                buffer &= (1 << bits) - 1;
            }
        }

        return [.. bytes];
    }
}
