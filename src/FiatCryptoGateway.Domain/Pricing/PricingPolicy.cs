using FiatCryptoGateway.Domain.Assets;

namespace FiatCryptoGateway.Domain.Pricing;

/// <summary>
/// Commercial terms applied to every quote. Basis points: 100 bps = 1%.
/// Order limits are expressed in the pivot currency so they mean the same thing for every fiat currency.
/// </summary>
public sealed record PricingPolicy(
    string Version,
    int ServiceFeeBasisPoints,
    int SpreadBasisPoints,
    TimeSpan QuoteTimeToLive,
    TimeSpan PaymentWindow,
    TimeSpan MaximumPriceAge,
    Asset PivotCurrency,
    decimal MinimumPivotAmount,
    decimal MaximumPivotAmount)
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Version);
        ArgumentNullException.ThrowIfNull(PivotCurrency);

        if (ServiceFeeBasisPoints is < 0 or >= 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(ServiceFeeBasisPoints), "The service fee must be between 0 and 9,999 basis points.");
        }

        if (SpreadBasisPoints is < 0 or >= 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(SpreadBasisPoints), "The spread must be between 0 and 9,999 basis points.");
        }

        if (QuoteTimeToLive <= TimeSpan.Zero || PaymentWindow <= TimeSpan.Zero || MaximumPriceAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(QuoteTimeToLive), "Quote, payment and price-age windows must be positive.");
        }

        if (PivotCurrency.Kind != AssetKind.Fiat)
        {
            throw new ArgumentException("The pivot currency must be fiat.", nameof(PivotCurrency));
        }

        if (MinimumPivotAmount <= 0 || MaximumPivotAmount < MinimumPivotAmount)
        {
            throw new ArgumentOutOfRangeException(nameof(MinimumPivotAmount), "Order limits must be positive, with the maximum at least the minimum.");
        }
    }
}
