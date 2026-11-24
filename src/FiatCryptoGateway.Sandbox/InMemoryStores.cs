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

public sealed class InMemoryOrderStore : IOrderStore
{
    private readonly ConcurrentDictionary<Guid, PaymentOrder> _orders = new();

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

    // Orders are held by reference, so there is nothing to write back in memory.
    public Task UpdateAsync(PaymentOrder order, CancellationToken cancellationToken) => Task.CompletedTask;

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
        lock (_gate)
        {
            _entries.Add(entry);
        }

        return Task.CompletedTask;
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
