using FiatCryptoGateway.Domain.Assets;

namespace FiatCryptoGateway.Domain.Pricing;

/// <summary>
/// One observed exchange rate: <see cref="Rate"/> units of <see cref="QuoteAsset"/> per one unit of
/// <see cref="BaseAsset"/>. It records when the price was observed and when the gateway could first use
/// it; quoting never uses a price before <see cref="AvailableAt"/>.
/// </summary>
public sealed record PriceObservation
{
    public PriceObservation(
        AssetId baseAsset,
        AssetId quoteAsset,
        decimal rate,
        DateTimeOffset observedAt,
        DateTimeOffset availableAt,
        string source)
    {
        ArgumentNullException.ThrowIfNull(baseAsset);
        ArgumentNullException.ThrowIfNull(quoteAsset);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        if (rate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rate), "A rate must be positive.");
        }

        if (availableAt < observedAt)
        {
            throw new ArgumentOutOfRangeException(nameof(availableAt), "A price cannot be available before it was observed.");
        }

        BaseAsset = baseAsset;
        QuoteAsset = quoteAsset;
        Rate = rate;
        ObservedAt = observedAt;
        AvailableAt = availableAt;
        Source = source;
    }

    public AssetId BaseAsset { get; }

    public AssetId QuoteAsset { get; }

    public decimal Rate { get; }

    public DateTimeOffset ObservedAt { get; }

    public DateTimeOffset AvailableAt { get; }

    public string Source { get; }

    /// <summary>The 1:1 rate of an asset to itself, used when the source currency is the pivot currency.</summary>
    public static PriceObservation Identity(AssetId asset, DateTimeOffset at) => new(asset, asset, 1m, at, at, "identity");
}
