using System.Globalization;
using FiatCryptoGateway.Domain.Assets;

namespace FiatCryptoGateway.Api.Checkout;

/// <summary>How amounts and times read to a paying customer.</summary>
public static class CustomerFormat
{
    /// <summary>Rupees as Indians write them (₹1,00,000.00); other currencies with thousands separators and their code.</summary>
    public static string Amount(Money money)
    {
        ArgumentNullException.ThrowIfNull(money);
        var text = money.ToFixedString();
        var point = text.IndexOf('.', StringComparison.Ordinal);
        var whole = point < 0 ? text : text[..point];
        var fraction = point < 0 ? string.Empty : text[point..];
        var isRupee = money.Asset.Id.Code == "INR";
        var grouped = Group(whole, isRupee);
        return isRupee ? $"₹{grouped}{fraction}" : $"{grouped}{fraction} {money.Asset.Id.Code}";
    }

    /// <summary>The time of day in <paramref name="zone"/>, with "IST" for India and the UTC offset elsewhere.</summary>
    public static string TimeOfDay(DateTimeOffset at, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var local = TimeZoneInfo.ConvertTime(at, zone);
        var label = zone.Id is "Asia/Kolkata" or "Asia/Calcutta" or "India Standard Time"
            ? "IST"
            : "UTC" + local.ToString("zzz", CultureInfo.InvariantCulture);
        return $"{local.ToString("HH:mm", CultureInfo.InvariantCulture)} {label}";
    }

    // Indian grouping: the last three digits, then pairs (12,34,567). Otherwise threes (1,234,567).
    private static string Group(string digits, bool indian)
    {
        if (digits.Length <= 3)
        {
            return digits;
        }

        var tail = digits[^3..];
        var head = digits[..^3];
        var size = indian ? 2 : 3;
        var groups = new List<string>();
        for (var end = head.Length; end > 0; end -= size)
        {
            groups.Insert(0, head[Math.Max(0, end - size)..end]);
        }

        return string.Join(',', groups) + "," + tail;
    }
}
