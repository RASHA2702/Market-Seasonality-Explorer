namespace FiatCryptoGateway.Domain.Assets;

public enum AssetKind
{
    Fiat,
    Crypto,
}

/// <summary>
/// Something the gateway can price, hold and move. <see cref="Scale"/> is the number of decimal
/// places: ISO 4217 minor units for fiat (JPY 0, USD 2, KWD 3) and on-chain decimals for crypto.
/// </summary>
public sealed record Asset
{
    private Asset(AssetId id, AssetKind kind, int scale, string name, AssetId? networkFeeAsset)
    {
        Id = id;
        Kind = kind;
        Scale = scale;
        Name = name;
        NetworkFeeAsset = networkFeeAsset;
    }

    public AssetId Id { get; }

    public AssetKind Kind { get; }

    public int Scale { get; }

    public string Name { get; }

    /// <summary>The asset network fees are paid in (ETH for USDC@ethereum). Null for fiat.</summary>
    public AssetId? NetworkFeeAsset { get; }

    public static Asset Fiat(string isoCode, int minorUnits, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfNegative(minorUnits);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minorUnits, 4);
        return new Asset(AssetId.Fiat(isoCode), AssetKind.Fiat, minorUnits, name, null);
    }

    public static Asset Crypto(AssetId id, int decimals, string name, AssetId networkFeeAsset)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(networkFeeAsset);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfNegative(decimals);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(decimals, 18);

        if (id.IsFiat)
        {
            throw new ArgumentException("A crypto asset must name its network.", nameof(id));
        }

        if (networkFeeAsset.IsFiat || networkFeeAsset.Network != id.Network)
        {
            throw new ArgumentException("Network fees must be paid in an asset on the same network.", nameof(networkFeeAsset));
        }

        return new Asset(id, AssetKind.Crypto, decimals, name, networkFeeAsset);
    }
}
