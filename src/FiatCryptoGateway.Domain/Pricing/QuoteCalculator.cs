using FiatCryptoGateway.Domain.Assets;

namespace FiatCryptoGateway.Domain.Pricing;

public sealed record QuoteInputs(
    string ClientId,
    Money SourceAmount,
    Asset DestinationAsset,
    IReadOnlyList<PriceObservation> PricePath,
    IReadOnlyList<PriceObservation> PivotRoute,
    Money NetworkFee,
    PricingPolicy Policy,
    DateTimeOffset Now);

/// <summary>
/// Turns a fiat amount into a quote for a crypto asset. Steps, in order:
/// 1. every price in the route must be available and fresh at quote time (no look-ahead, no stale prices);
/// 2. limits are checked in the pivot currency;
/// 3. the service fee is rounded up in the fiat currency;
/// 4. the spread is applied to the market rate and the crypto amount is rounded down;
/// 5. the network fee is deducted; the customer must receive a positive amount.
/// </summary>
public static class QuoteCalculator
{
    private const decimal BasisPointsPerUnit = 10_000m;
    private const decimal MaximumSourceAmount = 1_000_000_000_000m;

    public static Quote Calculate(QuoteInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputs.ClientId);
        ArgumentNullException.ThrowIfNull(inputs.Policy);
        ArgumentNullException.ThrowIfNull(inputs.PricePath);
        ArgumentNullException.ThrowIfNull(inputs.PivotRoute);

        var policy = inputs.Policy;
        policy.Validate();
        var source = inputs.SourceAmount;
        var destination = inputs.DestinationAsset;

        if (source.Asset.Kind != AssetKind.Fiat)
        {
            throw new GatewayRuleException("SOURCE_MUST_BE_FIAT", "The amount paid must be in a fiat currency.");
        }

        if (destination.Kind != AssetKind.Crypto)
        {
            throw new GatewayRuleException("DESTINATION_MUST_BE_CRYPTO", "The destination must be a crypto asset.");
        }

        if (!source.IsPositive || source.Amount > MaximumSourceAmount)
        {
            throw new GatewayRuleException("INVALID_AMOUNT", "The amount must be positive and realistic.");
        }

        if (inputs.NetworkFee.Asset != destination || inputs.NetworkFee.IsNegative)
        {
            throw new ArgumentException("The network fee must be a non-negative amount of the destination asset.", nameof(inputs));
        }

        var marketRate = ComposeRate(inputs.PricePath, source.Asset.Id, destination.Id, inputs.Now, policy.MaximumPriceAge);
        var pivotRate = ComposeRate(inputs.PivotRoute, source.Asset.Id, policy.PivotCurrency.Id, inputs.Now, policy.MaximumPriceAge);
        var pivotAmount = Money.RoundDown(source.Amount * pivotRate, policy.PivotCurrency);

        if (pivotAmount.Amount < policy.MinimumPivotAmount)
        {
            throw new GatewayRuleException(
                "BELOW_MINIMUM_AMOUNT", $"Orders must be worth at least {policy.MinimumPivotAmount} {policy.PivotCurrency.Id}.");
        }

        if (pivotAmount.Amount > policy.MaximumPivotAmount)
        {
            throw new GatewayRuleException(
                "ABOVE_MAXIMUM_AMOUNT", $"Orders must be worth at most {policy.MaximumPivotAmount} {policy.PivotCurrency.Id}.");
        }

        var serviceFee = Money.RoundUp(source.Amount * policy.ServiceFeeBasisPoints / BasisPointsPerUnit, source.Asset);
        var netSource = source.Subtract(serviceFee);
        if (!netSource.IsPositive)
        {
            throw new GatewayRuleException("AMOUNT_TOO_SMALL", "The amount does not cover the service fee.");
        }

        var customerRate = marketRate * (BasisPointsPerUnit - policy.SpreadBasisPoints) / BasisPointsPerUnit;
        var gross = Money.RoundDown(netSource.Amount * customerRate, destination);
        var delivered = gross.Subtract(inputs.NetworkFee);
        if (!delivered.IsPositive)
        {
            throw new GatewayRuleException("AMOUNT_TOO_SMALL_FOR_NETWORK_FEE", "The amount does not cover the network fee.");
        }

        return new Quote(
            Guid.NewGuid(),
            inputs.ClientId,
            source,
            serviceFee,
            netSource,
            marketRate,
            customerRate,
            gross,
            inputs.NetworkFee,
            delivered,
            pivotAmount,
            [.. inputs.PricePath],
            policy.Version,
            inputs.Now,
            inputs.Now + policy.QuoteTimeToLive);
    }

    private static decimal ComposeRate(
        IReadOnlyList<PriceObservation> path,
        AssetId from,
        AssetId to,
        DateTimeOffset now,
        TimeSpan maximumAge)
    {
        if (path.Count == 0)
        {
            throw new GatewayRuleException("NO_PRICE_ROUTE", $"No price route from {from} to {to}.");
        }

        var rate = 1m;
        var expected = from;
        foreach (var observation in path)
        {
            if (observation.BaseAsset != expected)
            {
                throw new GatewayRuleException("PRICE_ROUTE_BROKEN", $"The price route from {from} to {to} is not contiguous.");
            }

            if (observation.AvailableAt > now)
            {
                throw new GatewayRuleException("PRICE_NOT_YET_AVAILABLE", $"The {observation.BaseAsset}/{observation.QuoteAsset} price is not usable yet.");
            }

            if (now - observation.ObservedAt > maximumAge)
            {
                throw new GatewayRuleException("PRICE_STALE", $"The {observation.BaseAsset}/{observation.QuoteAsset} price is too old.");
            }

            rate *= observation.Rate;
            expected = observation.QuoteAsset;
        }

        if (expected != to)
        {
            throw new GatewayRuleException("PRICE_ROUTE_BROKEN", $"The price route does not end at {to}.");
        }

        return rate;
    }
}
