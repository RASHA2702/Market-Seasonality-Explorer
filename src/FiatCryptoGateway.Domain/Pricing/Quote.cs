using FiatCryptoGateway.Domain.Assets;

namespace FiatCryptoGateway.Domain.Pricing;

/// <summary>
/// A priced offer to convert a fiat amount into a crypto asset. Only <see cref="QuoteCalculator"/>
/// creates quotes, so every quote's figures satisfy:
/// SourceAmount = ServiceFee + NetSourceAmount, and GrossDestinationAmount = DestinationAmount + NetworkFee.
/// </summary>
public sealed class Quote
{
    internal Quote(
        Guid quoteId,
        string clientId,
        Money sourceAmount,
        Money serviceFee,
        Money netSourceAmount,
        decimal marketRate,
        decimal customerRate,
        Money grossDestinationAmount,
        Money networkFee,
        Money destinationAmount,
        Money pivotAmount,
        IReadOnlyList<PriceObservation> pricePath,
        string pricingPolicyVersion,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt)
    {
        QuoteId = quoteId;
        ClientId = clientId;
        SourceAmount = sourceAmount;
        ServiceFee = serviceFee;
        NetSourceAmount = netSourceAmount;
        MarketRate = marketRate;
        CustomerRate = customerRate;
        GrossDestinationAmount = grossDestinationAmount;
        NetworkFee = networkFee;
        DestinationAmount = destinationAmount;
        PivotAmount = pivotAmount;
        PricePath = pricePath;
        PricingPolicyVersion = pricingPolicyVersion;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
    }

    public Guid QuoteId { get; }

    public string ClientId { get; }

    /// <summary>Fiat the customer pays.</summary>
    public Money SourceAmount { get; }

    /// <summary>Fiat kept as the gateway's service fee.</summary>
    public Money ServiceFee { get; }

    /// <summary>Fiat converted into crypto.</summary>
    public Money NetSourceAmount { get; }

    /// <summary>Destination units per one source unit, before the gateway's spread.</summary>
    public decimal MarketRate { get; }

    /// <summary>Destination units per one source unit offered to the customer.</summary>
    public decimal CustomerRate { get; }

    /// <summary>Crypto bought for the customer before the network fee.</summary>
    public Money GrossDestinationAmount { get; }

    /// <summary>Network fee charged to the customer, in the destination asset.</summary>
    public Money NetworkFee { get; }

    /// <summary>Crypto delivered to the customer's wallet.</summary>
    public Money DestinationAmount { get; }

    /// <summary>The source amount in the pivot currency, used for limits and compliance thresholds.</summary>
    public Money PivotAmount { get; }

    public IReadOnlyList<PriceObservation> PricePath { get; }

    public string PricingPolicyVersion { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset ExpiresAt { get; }

    public bool IsExpiredAt(DateTimeOffset at) => at >= ExpiresAt;
}
