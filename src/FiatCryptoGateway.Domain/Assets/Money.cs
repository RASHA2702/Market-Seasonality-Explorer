using System.Globalization;

namespace FiatCryptoGateway.Domain.Assets;

/// <summary>
/// An exact amount of one asset. An amount never has more decimal places than its asset allows, so a
/// calculation that produces extra decimals must choose a rounding direction explicitly:
/// <see cref="RoundUp"/> for amounts the gateway collects (fees) and <see cref="RoundDown"/> for amounts
/// it delivers, so it never promises a fraction it cannot pay.
/// </summary>
public sealed record Money
{
    public Money(decimal amount, Asset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (decimal.Round(amount, asset.Scale) != amount)
        {
            throw new GatewayRuleException(
                "AMOUNT_PRECISION",
                $"{amount.ToString(CultureInfo.InvariantCulture)} has more decimal places than {asset.Id} allows ({asset.Scale}).");
        }

        Amount = amount;
        Asset = asset;
    }

    public decimal Amount { get; }

    public Asset Asset { get; }

    public bool IsPositive => Amount > 0;

    public bool IsNegative => Amount < 0;

    public static Money Zero(Asset asset) => new(0m, asset);

    /// <summary>Rounds toward positive infinity at the asset's scale.</summary>
    public static Money RoundUp(decimal amount, Asset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        return new Money(decimal.Round(amount, asset.Scale, MidpointRounding.ToPositiveInfinity), asset);
    }

    /// <summary>Rounds toward zero at the asset's scale.</summary>
    public static Money RoundDown(decimal amount, Asset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        return new Money(decimal.Round(amount, asset.Scale, MidpointRounding.ToZero), asset);
    }

    public Money Add(Money other)
    {
        EnsureSameAsset(other);
        return new Money(Amount + other.Amount, Asset);
    }

    public Money Subtract(Money other)
    {
        EnsureSameAsset(other);
        return new Money(Amount - other.Amount, Asset);
    }

    public Money Negate() => new(-Amount, Asset);

    /// <summary>Fixed-point text at the asset's scale, for example <c>1000.00</c> or <c>0.00012000</c>.</summary>
    public string ToFixedString() =>
        Amount.ToString("F" + Asset.Scale.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    public override string ToString() => $"{ToFixedString()} {Asset.Id}";

    private void EnsureSameAsset(Money other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (other.Asset != Asset)
        {
            throw new GatewayRuleException("ASSET_MISMATCH", $"Cannot combine {Asset.Id} with {other.Asset.Id}.");
        }
    }
}
