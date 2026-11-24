using FiatCryptoGateway.Domain.Assets;
using FiatCryptoGateway.Domain.Compliance;
using FiatCryptoGateway.Domain.Ledger;
using FiatCryptoGateway.Domain.Orders;
using FiatCryptoGateway.Domain.Pricing;

namespace FiatCryptoGateway.Application.Ports;

// Each interface is a seam for a real partner. This build ships only sandbox implementations;
// a production adapter (PSP, exchange, custodian, KYC vendor) implements the same interface.

/// <summary>Market prices with honest observed/available timestamps.</summary>
public interface IPriceFeed
{
    /// <summary>A contiguous route, for example INR→USD then USD→USDC@ethereum.</summary>
    Task<IReadOnlyList<PriceObservation>> GetRouteAsync(AssetId source, AssetId destination, CancellationToken cancellationToken);
}

public sealed record FiatPaymentSession(string SessionId, Uri CheckoutUrl);

/// <summary>Collects fiat from customers: a card acquirer, bank-transfer or local-payment-rail provider.</summary>
public interface IFiatPaymentProvider
{
    string Name { get; }

    Task<FiatPaymentSession> CreateSessionAsync(PaymentOrder order, CancellationToken cancellationToken);

    /// <summary>Must be idempotent on the order ID: a retry never refunds twice.</summary>
    Task<FiatRefund> RefundAsync(PaymentOrder order, Money amount, CancellationToken cancellationToken);
}

/// <summary>Buys crypto with fiat: an exchange or OTC desk.</summary>
public interface ILiquidityProvider
{
    string Name { get; }

    /// <summary>Buys the quoted asset with the order's net fiat. Must be idempotent on the order ID: a retry never buys twice.</summary>
    Task<LiquidityExecution> BuyAsync(PaymentOrder order, CancellationToken cancellationToken);
}

/// <summary>Holds crypto and sends it on-chain: a custodian or a self-custody wallet service.</summary>
public interface ICustodyProvider
{
    string Name { get; }

    bool IsValidAddress(Asset asset, string address);

    int RequiredConfirmations(Asset asset);

    /// <summary>The network fee charged to the customer, in the asset being delivered.</summary>
    Task<Money> EstimateCustomerNetworkFeeAsync(Asset asset, CancellationToken cancellationToken);

    /// <summary>Sends the quoted amount to the order's address. Must be idempotent on the order ID: a retry never sends twice.</summary>
    Task<CryptoTransfer> SendAsync(PaymentOrder order, CancellationToken cancellationToken);
}

/// <summary>KYC status and sanctions screening of customers, as held by the KYC provider.</summary>
public interface ICustomerDirectory
{
    Task<CustomerProfile?> FindAsync(string clientId, string customerId, CancellationToken cancellationToken);
}

/// <summary>Blockchain-analytics screening of destination wallets.</summary>
public interface IWalletScreening
{
    Task<WalletScreeningResult> ScreenAsync(Asset asset, string address, CancellationToken cancellationToken);
}

public interface IQuoteStore
{
    Task AddAsync(Quote quote, CancellationToken cancellationToken);

    Task<Quote?> FindAsync(Guid quoteId, CancellationToken cancellationToken);

    /// <summary>Atomically marks the quote as used by an order. Returns false if it was already used.</summary>
    Task<bool> TryConsumeAsync(Guid quoteId, Guid orderId, CancellationToken cancellationToken);
}

public interface IOrderStore
{
    Task AddAsync(PaymentOrder order, CancellationToken cancellationToken);

    Task<PaymentOrder?> FindAsync(Guid orderId, CancellationToken cancellationToken);

    Task UpdateAsync(PaymentOrder order, CancellationToken cancellationToken);

    Task<IReadOnlyList<Guid>> FindByStatusAsync(IReadOnlyCollection<OrderStatus> statuses, CancellationToken cancellationToken);
}

/// <summary>Append-only journal. There is deliberately no update or delete.</summary>
public interface ILedgerStore
{
    Task AppendAsync(JournalEntry entry, CancellationToken cancellationToken);

    Task<IReadOnlyList<JournalEntry>> ListAsync(Guid? orderId, CancellationToken cancellationToken);
}

public sealed record StoredResponse(int StatusCode, string Body);

/// <summary>A claimed idempotency key. <see cref="Response"/> is null while the first request is still running.</summary>
public sealed record IdempotencyRecord(string RequestHash, StoredResponse? Response);

public interface IIdempotencyStore
{
    /// <summary>Claims the key. Returns null when this request now owns it, or the existing record otherwise.</summary>
    Task<IdempotencyRecord?> TryClaimAsync(string clientId, string key, string requestHash, CancellationToken cancellationToken);

    Task CompleteAsync(string clientId, string key, StoredResponse response, CancellationToken cancellationToken);

    /// <summary>Releases a claim whose request failed before producing a response, so the client can retry.</summary>
    Task ReleaseAsync(string clientId, string key, CancellationToken cancellationToken);
}

/// <summary>Deduplicates provider webhooks, which providers deliver at least once.</summary>
public interface IWebhookInbox
{
    /// <summary>Claims a provider event. Returns false when it was already received.</summary>
    Task<bool> TryClaimAsync(string provider, string eventId, string payloadSha256, CancellationToken cancellationToken);

    Task ReleaseAsync(string provider, string eventId, CancellationToken cancellationToken);
}
