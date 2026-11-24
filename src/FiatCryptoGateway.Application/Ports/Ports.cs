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

    /// <summary>
    /// Saves the order's new state, its new history and the journal entries for that change in one
    /// transaction, so a crash can never record one without the other. Throws
    /// <see cref="OrderConcurrencyException"/> if the order changed since it was loaded.
    /// </summary>
    Task SaveAsync(PaymentOrder order, IReadOnlyList<JournalEntry> journalEntries, CancellationToken cancellationToken);

    Task<IReadOnlyList<Guid>> FindByStatusAsync(IReadOnlyCollection<OrderStatus> statuses, CancellationToken cancellationToken);

    /// <summary>
    /// The customer's payments in the pivot currency since each cut-off, not counting expired or refunded
    /// orders, payments priced in another pivot currency, or the order being screened.
    /// </summary>
    Task<CustomerVolume> GetCustomerVolumeAsync(
        string clientId,
        string customerId,
        AssetId pivotCurrency,
        DateTimeOffset daySince,
        DateTimeOffset monthSince,
        Guid excludeOrderId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Serialises all work on one order. The database implementation holds the lock across API instances,
/// so two instances can never call a provider for the same order at the same time.
/// </summary>
public interface IOrderLocks
{
    Task<IAsyncDisposable> AcquireAsync(Guid orderId, CancellationToken cancellationToken);
}

public sealed class OrderConcurrencyException : Exception
{
    public OrderConcurrencyException()
        : this("The order was changed by another process.")
    {
    }

    public OrderConcurrencyException(string message)
        : base(message)
    {
    }

    public OrderConcurrencyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public enum ApiClientRole
{
    Merchant,
    Reviewer,
}

public sealed record ApiClientRecord(string ClientId, string KeySha256, ApiClientRole Role);

/// <summary>Prepares storage at startup: schema migrations and reference data (assets, API clients).</summary>
public interface IStorageInitializer
{
    Task InitializeAsync(IEnumerable<Asset> assets, IEnumerable<ApiClientRecord> clients, CancellationToken cancellationToken);
}

/// <summary>A notification waiting to be sent to a merchant, written in the same transaction as the order change.</summary>
public sealed record OutboxMessage(
    Guid MessageId,
    string ClientId,
    Guid OrderId,
    int Sequence,
    string EventType,
    string Payload,
    int Attempts,
    DateTimeOffset OccurredAt);

/// <summary>Delivery state of one notification. <c>Attempts</c> counts every try, including a successful one.</summary>
public sealed record OutboxMessageStatus(
    Guid MessageId,
    int Sequence,
    int Attempts,
    DateTimeOffset? DeliveredAt,
    DateTimeOffset? DeadAt,
    string? LastError);

/// <summary>Merchant notifications. Stores add messages when they save an order; the dispatcher sends them.</summary>
public interface IOutbox
{
    /// <summary>
    /// Claims up to <paramref name="limit"/> due messages, at most the earliest pending one per order (so
    /// each order's events go out in sequence), and hides them from other instances for <paramref name="lease"/>.
    /// </summary>
    Task<IReadOnlyList<OutboxMessage>> ClaimDueAsync(DateTimeOffset now, TimeSpan lease, int limit, CancellationToken cancellationToken);

    Task MarkDeliveredAsync(Guid messageId, DateTimeOffset at, CancellationToken cancellationToken);

    Task ScheduleRetryAsync(Guid messageId, DateTimeOffset nextAttemptAt, string failure, CancellationToken cancellationToken);

    /// <summary>Stops retrying a message; it stays recorded with the last error.</summary>
    Task MarkDeadAsync(Guid messageId, string failure, DateTimeOffset at, CancellationToken cancellationToken);

    Task<IReadOnlyList<OutboxMessageStatus>> ListForOrderAsync(Guid orderId, CancellationToken cancellationToken);
}

public sealed record MerchantWebhookTarget(Uri Url, string Secret);

/// <summary>Where each merchant receives notifications, and the secret their signatures use.</summary>
public interface IMerchantWebhookTargets
{
    MerchantWebhookTarget? Find(string clientId);
}

public sealed record WebhookDeliveryResult(bool Succeeded, string? Error)
{
    public static WebhookDeliveryResult Success { get; } = new(true, null);

    public static WebhookDeliveryResult Failed(string error) => new(false, error);
}

public interface IWebhookSender
{
    Task<WebhookDeliveryResult> SendAsync(Uri url, Guid eventId, string body, string signature, CancellationToken cancellationToken);
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
