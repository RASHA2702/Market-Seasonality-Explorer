using System.Collections.Concurrent;
using FiatCryptoGateway.Application.Ports;
using FiatCryptoGateway.Application.Reconciliation;
using FiatCryptoGateway.Application.Webhooks;
using FiatCryptoGateway.Domain.Assets;
using FiatCryptoGateway.Domain.Compliance;
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

public sealed class InMemoryOrderStore(InMemoryLedgerStore ledger, InMemoryOutbox outbox) : IOrderStore
{
    private readonly ConcurrentDictionary<Guid, PaymentOrder> _orders = new();
    private readonly ConcurrentDictionary<Guid, int> _storedEvents = new();
    private readonly Lock _saveGate = new();

    public Task AddAsync(PaymentOrder order, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);
        lock (_saveGate)
        {
            if (!_orders.TryAdd(order.OrderId, order))
            {
                throw new InvalidOperationException($"Order {order.OrderId} already exists.");
            }

            outbox.Enqueue(MerchantNotifications.ForNewEvents(order, storedEvents: 0));
            _storedEvents[order.OrderId] = order.Events.Count;
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
            outbox.Enqueue(MerchantNotifications.ForNewEvents(order, _storedEvents[order.OrderId]));
            _storedEvents[order.OrderId] = order.Events.Count;
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

    public Task<CustomerVolume> GetCustomerVolumeAsync(
        string clientId,
        string customerId,
        AssetId pivotCurrency,
        DateTimeOffset daySince,
        DateTimeOffset monthSince,
        Guid excludeOrderId,
        CancellationToken cancellationToken)
    {
        var counted = _orders.Values
            .Where(order => order.OrderId != excludeOrderId &&
                            string.Equals(order.ClientId, clientId, StringComparison.Ordinal) &&
                            string.Equals(order.CustomerId, customerId, StringComparison.Ordinal) &&
                            order.Status is not (OrderStatus.Expired or OrderStatus.Refunded) &&
                            order.Quote.PivotAmount.Asset.Id == pivotCurrency)
            .ToList();
        return Task.FromResult(new CustomerVolume(
            counted.Where(order => order.CreatedAt >= daySince).Sum(order => order.Quote.PivotAmount.Amount),
            counted.Where(order => order.CreatedAt >= monthSince).Sum(order => order.Quote.PivotAmount.Amount)));
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

    public Task<IReadOnlyList<JournalEntry>> ListForReconciliationAsync(
        string provider,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        IReadOnlyCollection<string> references,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(references);
        lock (_gate)
        {
            IReadOnlyList<JournalEntry> entries = _entries
                .Where(entry => entry.External is { } external &&
                                string.Equals(external.Provider, provider, StringComparison.Ordinal) &&
                                ((external.OccurredAt >= windowStart && external.OccurredAt < windowEnd) || references.Contains(external.Id)))
                .ToList()
                .AsReadOnly();
            return Task.FromResult(entries);
        }
    }
}

public sealed class InMemoryReconciliationStore : IReconciliationStore
{
    private readonly List<ReconciliationRun> _runs = [];
    private readonly Lock _gate = new();

    public Task SaveAsync(ReconciliationRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        lock (_gate)
        {
            _runs.Add(run);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ReconciliationRun>> ListAsync(DateOnly? day, int limit, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            // Newest first; runs made at the same moment keep the later one first (the sort is stable).
            IReadOnlyList<ReconciliationRun> runs = Enumerable.Reverse(_runs)
                .Where(run => day is null || run.Day == day)
                .OrderByDescending(run => run.RanAt)
                .Take(limit)
                .ToList()
                .AsReadOnly();
            return Task.FromResult(runs);
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

/// <summary>Process-memory outbox with the same delivery rules as the PostgreSQL one.</summary>
public sealed class InMemoryOutbox : IOutbox
{
    private readonly List<Entry> _entries = [];
    private readonly Lock _gate = new();

    public Task<IReadOnlyList<OutboxMessage>> ClaimDueAsync(DateTimeOffset now, TimeSpan lease, int limit, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var claimed = _entries
                .Where(entry => entry.DeliveredAt is null && entry.DeadAt is null)
                .GroupBy(entry => entry.Message.OrderId)
                .Select(order => order.OrderBy(entry => entry.Message.Sequence).First())
                .Where(entry => entry.NextAttemptAt <= now)
                .OrderBy(entry => entry.NextAttemptAt)
                .Take(limit)
                .ToList();
            foreach (var entry in claimed)
            {
                entry.NextAttemptAt = now + lease;
            }

            IReadOnlyList<OutboxMessage> messages = claimed.Select(entry => entry.Message with { Attempts = entry.Attempts }).ToList().AsReadOnly();
            return Task.FromResult(messages);
        }
    }

    public Task MarkDeliveredAsync(Guid messageId, DateTimeOffset at, CancellationToken cancellationToken) =>
        Update(messageId, entry =>
        {
            entry.Attempts++;
            entry.DeliveredAt = at;
        });

    public Task ScheduleRetryAsync(Guid messageId, DateTimeOffset nextAttemptAt, string failure, CancellationToken cancellationToken) =>
        Update(messageId, entry =>
        {
            entry.Attempts++;
            entry.NextAttemptAt = nextAttemptAt;
            entry.LastError = failure;
        });

    public Task MarkDeadAsync(Guid messageId, string failure, DateTimeOffset at, CancellationToken cancellationToken) =>
        Update(messageId, entry =>
        {
            entry.Attempts++;
            entry.DeadAt = at;
            entry.LastError = failure;
        });

    public Task<IReadOnlyList<OutboxMessageStatus>> ListForOrderAsync(Guid orderId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            IReadOnlyList<OutboxMessageStatus> statuses = _entries
                .Where(entry => entry.Message.OrderId == orderId)
                .OrderBy(entry => entry.Message.Sequence)
                .Select(entry => new OutboxMessageStatus(
                    entry.Message.MessageId, entry.Message.Sequence, entry.Attempts, entry.DeliveredAt, entry.DeadAt, entry.LastError))
                .ToList()
                .AsReadOnly();
            return Task.FromResult(statuses);
        }
    }

    internal void Enqueue(IEnumerable<OutboxMessage> messages)
    {
        lock (_gate)
        {
            _entries.AddRange(messages.Select(message => new Entry(message) { NextAttemptAt = message.OccurredAt }));
        }
    }

    private Task Update(Guid messageId, Action<Entry> change)
    {
        lock (_gate)
        {
            change(_entries.Single(entry => entry.Message.MessageId == messageId));
        }

        return Task.CompletedTask;
    }

    private sealed class Entry(OutboxMessage message)
    {
        public OutboxMessage Message { get; } = message;

        public DateTimeOffset NextAttemptAt { get; set; }

        public int Attempts { get; set; }

        public DateTimeOffset? DeliveredAt { get; set; }

        public DateTimeOffset? DeadAt { get; set; }

        public string? LastError { get; set; }
    }
}
