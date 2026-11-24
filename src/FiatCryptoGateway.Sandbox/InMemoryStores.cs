using System.Collections.Concurrent;
using FiatCryptoGateway.Application.Ports;
using FiatCryptoGateway.Domain.Ledger;
using FiatCryptoGateway.Domain.Orders;
using FiatCryptoGateway.Domain.Pricing;

namespace FiatCryptoGateway.Sandbox;

// Process-memory stores for the sandbox. Everything is lost on restart. The PostgreSQL schema in
// db/migrations defines the durable equivalents, with the same rules enforced by constraints.

public sealed class InMemoryQuoteStore : IQuoteStore
{
    private readonly ConcurrentDictionary<Guid, Quote> _quotes = new();
    private readonly ConcurrentDictionary<Guid, Guid> _consumedBy = new();

    public Task AddAsync(Quote quote, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(quote);
        if (!_quotes.TryAdd(quote.QuoteId, quote))
        {
            throw new InvalidOperationException($"Quote {quote.QuoteId} already exists.");
        }

        return Task.CompletedTask;
    }

    public Task<Quote?> FindAsync(Guid quoteId, CancellationToken cancellationToken) =>
        Task.FromResult(_quotes.TryGetValue(quoteId, out var quote) ? quote : null);

    public Task<bool> TryConsumeAsync(Guid quoteId, Guid orderId, CancellationToken cancellationToken) =>
        Task.FromResult(_consumedBy.TryAdd(quoteId, orderId));
}

public sealed class InMemoryOrderStore(InMemoryLedgerStore ledger) : IOrderStore
{
    private readonly ConcurrentDictionary<Guid, PaymentOrder> _orders = new();
    private readonly Lock _saveGate = new();

    public Task AddAsync(PaymentOrder order, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);
        if (!_orders.TryAdd(order.OrderId, order))
        {
            throw new InvalidOperationException($"Order {order.OrderId} already exists.");
        }

        return Task.CompletedTask;
    }

    public Task<PaymentOrder?> FindAsync(Guid orderId, CancellationToken cancellationToken) =>
        Task.FromResult(_orders.TryGetValue(orderId, out var order) ? order : null);

    // Orders are held by reference, so only the journal entries need recording, together with the change.
    public Task SaveAsync(PaymentOrder order, IReadOnlyList<JournalEntry> journalEntries, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(journalEntries);
        lock (_saveGate)
        {
            if (!_orders.ContainsKey(order.OrderId))
            {
                throw new InvalidOperationException($"Order {order.OrderId} was never added.");
            }

            ledger.AppendAll(journalEntries);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Guid>> FindByStatusAsync(IReadOnlyCollection<OrderStatus> statuses, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(statuses);
        IReadOnlyList<Guid> matches = _orders.Values
            .Where(order => statuses.Contains(order.Status))
            .Select(order => order.OrderId)
            .ToList()
            .AsReadOnly();
        return Task.FromResult(matches);
    }
}

public sealed class InMemoryLedgerStore : ILedgerStore
{
    private readonly List<JournalEntry> _entries = [];
    private readonly Lock _gate = new();

    public Task AppendAsync(JournalEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        AppendAll([entry]);
        return Task.CompletedTask;
    }

    internal void AppendAll(IEnumerable<JournalEntry> entries)
    {
        lock (_gate)
        {
            _entries.AddRange(entries);
        }
    }

    public Task<IReadOnlyList<JournalEntry>> ListAsync(Guid? orderId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            IReadOnlyList<JournalEntry> entries = _entries
                .Where(entry => orderId is null || entry.OrderId == orderId)
                .ToList()
                .AsReadOnly();
            return Task.FromResult(entries);
        }
    }
}

public sealed class InMemoryIdempotencyStore : IIdempotencyStore
{
    private readonly ConcurrentDictionary<(string ClientId, string Key), IdempotencyRecord> _records = new();

    public Task<IdempotencyRecord?> TryClaimAsync(string clientId, string key, string requestHash, CancellationToken cancellationToken)
    {
        var claim = new IdempotencyRecord(requestHash, null);
        var stored = _records.GetOrAdd((clientId, key), claim);
        return Task.FromResult(ReferenceEquals(stored, claim) ? null : stored);
    }

    public Task CompleteAsync(string clientId, string key, StoredResponse response, CancellationToken cancellationToken)
    {
        _records.AddOrUpdate(
            (clientId, key),
            _ => throw new InvalidOperationException("Complete was called for an unclaimed idempotency key."),
            (_, existing) => existing with { Response = response });
        return Task.CompletedTask;
    }

    public Task ReleaseAsync(string clientId, string key, CancellationToken cancellationToken)
    {
        _records.TryRemove((clientId, key), out _);
        return Task.CompletedTask;
    }
}

public sealed class InMemoryWebhookInbox : IWebhookInbox
{
    private readonly ConcurrentDictionary<(string Provider, string EventId), string> _events = new();

    public Task<bool> TryClaimAsync(string provider, string eventId, string payloadSha256, CancellationToken cancellationToken) =>
        Task.FromResult(_events.TryAdd((provider, eventId), payloadSha256));

    public Task ReleaseAsync(string provider, string eventId, CancellationToken cancellationToken)
    {
        _events.TryRemove((provider, eventId), out _);
        return Task.CompletedTask;
    }
}

/// <summary>Nothing to prepare for process-memory storage.</summary>
public sealed class InMemoryStorageInitializer : IStorageInitializer
{
    public Task InitializeAsync(IEnumerable<Domain.Assets.Asset> assets, IEnumerable<ApiClientRecord> clients, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
