using System.Text.RegularExpressions;

namespace FiatCryptoGateway.Domain.Assets;

/// <summary>
/// Identity of anything the gateway can hold. Fiat is identified by its ISO 4217 code. A crypto asset
/// is identified by symbol AND network: USDC on Ethereum and USDC on Solana are different assets with
/// different addresses, fees and settlement, so a ticker alone is never enough.
/// </summary>
public sealed partial record AssetId
{
    private AssetId(string code, string? network)
    {
        Code = code;
        Network = network;
    }

    public string Code { get; }

    public string? Network { get; }

    public bool IsFiat => Network is null;

    public static AssetId Fiat(string isoCode)
    {
        ArgumentNullException.ThrowIfNull(isoCode);
        if (!FiatCodePattern().IsMatch(isoCode))
        {
            throw new GatewayRuleException("INVALID_ASSET", $"'{isoCode}' is not an upper-case ISO 4217 currency code.");
        }

        return new AssetId(isoCode, null);
    }

    public static AssetId Crypto(string symbol, string network)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        ArgumentNullException.ThrowIfNull(network);
        if (!CryptoSymbolPattern().IsMatch(symbol) || !NetworkPattern().IsMatch(network))
        {
            throw new GatewayRuleException("INVALID_ASSET", $"'{symbol}@{network}' is not a valid crypto asset; use SYMBOL@network.");
        }

        return new AssetId(symbol, network);
    }

    /// <summary>Parses <c>USD</c> (fiat) or <c>USDC@ethereum</c> (crypto symbol and network).</summary>
    public static AssetId Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var separator = value.IndexOf('@');
        return separator < 0 ? Fiat(value) : Crypto(value[..separator], value[(separator + 1)..]);
    }

    public override string ToString() => Network is null ? Code : $"{Code}@{Network}";

    [GeneratedRegex("^[A-Z]{3}$", RegexOptions.CultureInvariant)]
    private static partial Regex FiatCodePattern();

    [GeneratedRegex("^[A-Z0-9]{2,10}$", RegexOptions.CultureInvariant)]
    private static partial Regex CryptoSymbolPattern();

    [GeneratedRegex("^[a-z0-9-]{2,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex NetworkPattern();
}
