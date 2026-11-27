namespace FiatCryptoGateway.Domain.Payments;

/// <summary>How a customer pays. Netbanking and bank transfer join these later.</summary>
public enum PaymentMethod
{
    Card,
    Upi,
}

/// <summary>
/// What is kept about the way a customer paid. Never a card number or security code: a card is described by its brand
/// and last four digits, a UPI payment by its address. The label and last digits are checked here, so no code path
/// can store something that looks like a full card number in a payment attempt.
/// </summary>
public sealed record PaymentInstrument
{
    public const int MaxLabelLength = 64;

    public PaymentInstrument(PaymentMethod method, string label, string? brand = null, string? last4 = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        if (label.Length > MaxLabelLength || CardNumbers.ContainsCardLikeNumber(label))
        {
            throw new ArgumentException($"An instrument label has at most {MaxLabelLength} characters and holds no card number.", nameof(label));
        }

        if (brand is { Length: > 32 } || (brand is not null && CardNumbers.ContainsCardLikeNumber(brand)))
        {
            throw new ArgumentException("A brand is a short name.", nameof(brand));
        }

        if (last4 is not null && (last4.Length != 4 || !last4.All(char.IsAsciiDigit)))
        {
            throw new ArgumentException("The last digits are exactly four digits.", nameof(last4));
        }

        Method = method;
        Label = label;
        Brand = brand;
        Last4 = last4;
    }

    public PaymentMethod Method { get; }

    /// <summary>Shown to the customer and reviewers, for example <c>Visa ····1111</c> or <c>success@sandbox</c>.</summary>
    public string Label { get; }

    public string? Brand { get; }

    public string? Last4 { get; }
}

/// <summary>Card number rules shared by everything that must recognise, and refuse to keep, a card number.</summary>
public static class CardNumbers
{
    public const int MinimumLength = 12;
    public const int MaximumLength = 19;

    /// <summary>
    /// The digits of <paramref name="text"/> if it is a card number as people type it (digits, with spaces or hyphens
    /// between groups); otherwise null.
    /// </summary>
    public static string? Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var digits = new char[text.Length];
        var count = 0;
        foreach (var character in text)
        {
            if (character is ' ' or '-')
            {
                continue;
            }

            if (!char.IsAsciiDigit(character) || count == digits.Length)
            {
                return null;
            }

            digits[count++] = character;
        }

        return count is >= MinimumLength and <= MaximumLength ? new string(digits, 0, count) : null;
    }

    /// <summary>The Luhn check every card number passes. It catches typing slips, not fake numbers.</summary>
    public static bool PassesLuhn(string digits)
    {
        ArgumentNullException.ThrowIfNull(digits);
        var sum = 0;
        var doubleIt = false;
        for (var index = digits.Length - 1; index >= 0; index--)
        {
            if (!char.IsAsciiDigit(digits[index]))
            {
                return false;
            }

            var digit = digits[index] - '0';
            if (doubleIt)
            {
                digit *= 2;
                if (digit > 9)
                {
                    digit -= 9;
                }
            }

            sum += digit;
            doubleIt = !doubleIt;
        }

        return digits.Length > 0 && sum % 10 == 0;
    }

    /// <summary>Whether <paramref name="text"/> holds a run of 13 or more digits (spaces and hyphens ignored), as a card number would.</summary>
    public static bool ContainsCardLikeNumber(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var run = 0;
        foreach (var character in text)
        {
            if (char.IsAsciiDigit(character))
            {
                if (++run >= 13)
                {
                    return true;
                }
            }
            else if (character is not (' ' or '-'))
            {
                run = 0;
            }
        }

        return false;
    }
}
