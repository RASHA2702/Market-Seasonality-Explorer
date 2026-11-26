using FiatCryptoGateway.Application.Ports;
using FiatCryptoGateway.Application.Reconciliation;
using FiatCryptoGateway.Application.Webhooks;
using FiatCryptoGateway.Domain.Assets;
using FiatCryptoGateway.Domain.Compliance;
using FiatCryptoGateway.Domain.Ledger;
using FiatCryptoGateway.Domain.Orders;
using FiatCryptoGateway.Domain.Pricing;
using Npgsql;

namespace FiatCryptoGateway.Persistence.Postgres;

public sealed class PostgresQuoteStore(NpgsqlDataSource dataSource, AssetCatalog catalog) : IQuoteStore
{
    private readonly RecordMapper _mapper = new(catalog);

    public async Task AddAsync(Quote quote, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(quote);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await QuoteRows.InsertAsync(connection, quote, cancellationToken);
    }

    public async Task<Quote?> FindAsync(Guid quoteId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await QuoteRows.ReadAsync(connection, quoteId, _mapper, cancellationToken);
    }

    public async Task<bool> TryConsumeAsync(Guid quoteId, Guid orderId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "UPDATE gateway.quote SET consumed_by_order_id = @order_id WHERE quote_id = @quote_id AND consumed_by_order_id IS NULL",
            connection);
        command.With("order_id", orderId).With("quote_id", quoteId);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }
}

/// <summary>
/// Orders with their history. <see cref="SaveAsync"/> locks the row (<c>SELECT … FOR UPDATE</c>), checks the
/// version loaded earlier, and writes the new state, the new history rows and the journal entries in one
/// transaction.
/// </summary>
public sealed class PostgresOrderStore(NpgsqlDataSource dataSource, AssetCatalog catalog) : IOrderStore
{
    private readonly RecordMapper _mapper = new(catalog);

    public async Task AddAsync(PaymentOrder order, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var insert = new NpgsqlCommand(
            """
            INSERT INTO gateway.payment_order (
                order_id, client_id, customer_id, quote_id, destination_address, status, fiat_session_id, checkout_url,
                payment_deadline, fiat_provider, fiat_payment_id, transaction_hash, confirmations, created_at, updated_at,
                row_version, fiat_payment, purchase, transfer, refund)
            VALUES (
                @id, @client, @customer, @quote, @destination, @status, @session, @checkout,
                @deadline, @fiat_provider, @fiat_payment_id, @transaction_hash, @confirmations, @created_at, @updated_at,
                0, @fiat_payment, @purchase, @transfer, @refund)
            """,
            connection,
            transaction))
        {
            BindState(insert, order)
                .With("client", order.ClientId)
                .With("customer", order.CustomerId)
                .With("quote", order.Quote.QuoteId)
                .With("destination", order.DestinationAddress)
                .With("deadline", order.PaymentDeadline.Utc())
                .With("created_at", order.CreatedAt.Utc());
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertHistoryAsync(connection, transaction, order, storedEvents: 0, storedDecisions: 0, storedReviews: 0, cancellationToken);
        await OutboxRows.InsertAsync(connection, transaction, MerchantNotifications.ForNewEvents(order, storedEvents: 0), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        order.Version = 0;
    }

    public async Task SaveAsync(PaymentOrder order, IReadOnlyList<JournalEntry> journalEntries, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(journalEntries);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        int storedVersion, storedEvents, storedDecisions, storedReviews;
        await using (var locked = new NpgsqlCommand(
            """
            SELECT row_version,
                   (SELECT coalesce(max(sequence), 0) FROM gateway.order_event e WHERE e.order_id = o.order_id),
                   (SELECT coalesce(max(sequence), 0) FROM gateway.compliance_decision d WHERE d.order_id = o.order_id),
                   (SELECT coalesce(max(sequence), 0) FROM gateway.review_action r WHERE r.order_id = o.order_id)
              FROM gateway.payment_order o
             WHERE o.order_id = @id
               FOR UPDATE
            """,
            connection,
            transaction))
        {
            locked.With("id", order.OrderId);
            await using var reader = await locked.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException($"Order {order.OrderId} was never added.");
            }

            (storedVersion, storedEvents, storedDecisions, storedReviews) =
                (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3));
        }

        if (storedVersion != order.Version)
        {
            throw new OrderConcurrencyException(
                $"Order {order.OrderId} is at version {storedVersion} in the database but was loaded at version {order.Version}.");
        }

        await using (var update = new NpgsqlCommand(
            """
            UPDATE gateway.payment_order
               SET status = @status, fiat_session_id = @session, checkout_url = @checkout,
                   fiat_provider = @fiat_provider, fiat_payment_id = @fiat_payment_id, transaction_hash = @transaction_hash,
                   confirmations = @confirmations, updated_at = @updated_at, fiat_payment = @fiat_payment,
                   purchase = @purchase, transfer = @transfer, refund = @refund, row_version = row_version + 1
             WHERE order_id = @id
            """,
            connection,
            transaction))
        {
            await BindState(update, order).ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertHistoryAsync(connection, transaction, order, storedEvents, storedDecisions, storedReviews, cancellationToken);
        await LedgerRows.InsertAsync(connection, transaction, journalEntries, cancellationToken);
        await OutboxRows.InsertAsync(connection, transaction, MerchantNotifications.ForNewEvents(order, storedEvents), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        order.Version = storedVersion + 1;
    }

    public async Task<PaymentOrder?> FindAsync(Guid orderId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        Guid quoteId;
        string clientId, customerId, destination, status;
        string? sessionId, checkoutUrl, fiatPayment, purchase, transfer, refund;
        DateTimeOffset deadline, createdAt, updatedAt;
        int confirmations, version;
        await using (var command = new NpgsqlCommand(
            """
            SELECT client_id, customer_id, quote_id, destination_address, status, fiat_session_id, checkout_url,
                   payment_deadline, confirmations, created_at, updated_at, row_version,
                   fiat_payment::text, purchase::text, transfer::text, refund::text
              FROM gateway.payment_order
             WHERE order_id = @id
            """,
            connection))
        {
            command.With("id", orderId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            clientId = reader.GetString(0);
            customerId = reader.GetString(1);
            quoteId = reader.GetGuid(2);
            destination = reader.GetString(3);
            status = reader.GetString(4);
            sessionId = reader.IsDBNull(5) ? null : reader.GetString(5);
            checkoutUrl = reader.IsDBNull(6) ? null : reader.GetString(6);
            deadline = reader.GetFieldValue<DateTimeOffset>(7);
            confirmations = reader.GetInt32(8);
            createdAt = reader.GetFieldValue<DateTimeOffset>(9);
            updatedAt = reader.GetFieldValue<DateTimeOffset>(10);
            version = reader.GetInt32(11);
            fiatPayment = reader.IsDBNull(12) ? null : reader.GetString(12);
            purchase = reader.IsDBNull(13) ? null : reader.GetString(13);
            transfer = reader.IsDBNull(14) ? null : reader.GetString(14);
            refund = reader.IsDBNull(15) ? null : reader.GetString(15);
        }

        var quote = await QuoteRows.ReadAsync(connection, quoteId, _mapper, cancellationToken)
            ?? throw new InvalidOperationException($"Order {orderId} references missing quote {quoteId}.");

        var events = new List<OrderEvent>();
        await using (var command = new NpgsqlCommand(
            "SELECT sequence, from_status, to_status, occurred_at, reason FROM gateway.order_event WHERE order_id = @id ORDER BY sequence",
            connection))
        {
            command.With("id", orderId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                events.Add(new OrderEvent(
                    reader.GetInt32(0),
                    reader.IsDBNull(1) ? null : DbNames.Parse<OrderStatus>(reader.GetString(1)),
                    DbNames.Parse<OrderStatus>(reader.GetString(2)),
                    reader.GetFieldValue<DateTimeOffset>(3),
                    reader.GetString(4)));
            }
        }

        var decisions = new List<ComplianceDecision>();
        await using (var command = new NpgsqlCommand(
            "SELECT outcome, reasons::text, screener, decided_at FROM gateway.compliance_decision WHERE order_id = @id ORDER BY sequence",
            connection))
        {
            command.With("id", orderId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                decisions.Add(ComplianceRows.Read(reader, orderId));
            }
        }

        var reviews = new List<ReviewAction>();
        await using (var command = new NpgsqlCommand(
            """
            SELECT sequence, action, reviewer, reason, report_reference, occurred_at
              FROM gateway.review_action
             WHERE order_id = @id
             ORDER BY sequence
            """,
            connection))
        {
            command.With("id", orderId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                reviews.Add(new ReviewAction(
                    reader.GetInt32(0),
                    DbNames.Parse<ReviewActionKind>(reader.GetString(1)),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetFieldValue<DateTimeOffset>(5)));
            }
        }

        var order = PaymentOrder.Restore(
            orderId,
            clientId,
            customerId,
            quote,
            destination,
            createdAt,
            updatedAt,
            deadline,
            sessionId,
            checkoutUrl is null ? null : new Uri(checkoutUrl),
            _mapper.FiatPayment(fiatPayment, orderId),
            _mapper.Purchase(purchase, orderId),
            _mapper.Transfer(transfer, orderId),
            _mapper.Refund(refund, orderId),
            confirmations,
            decisions,
            reviews,
            events,
            version);

        if (DbNames.Of(order.Status) != status)
        {
            throw new InvalidOperationException($"Order {orderId} has status {status} but its history ends at {order.Status}.");
        }

        return order;
    }

    public async Task<IReadOnlyList<Guid>> FindByStatusAsync(IReadOnlyCollection<OrderStatus> statuses, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(statuses);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT order_id FROM gateway.payment_order WHERE status = ANY(@statuses) ORDER BY updated_at",
            connection);
        command.With("statuses", statuses.Select(DbNames.Of).ToArray());
        var ids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids.AsReadOnly();
    }

    public async Task<CustomerVolume> GetCustomerVolumeAsync(
        string clientId,
        string customerId,
        AssetId pivotCurrency,
        DateTimeOffset daySince,
        DateTimeOffset monthSince,
        Guid excludeOrderId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pivotCurrency);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT coalesce(sum(q.pivot_amount) FILTER (WHERE o.created_at >= @day_since), 0),
                   coalesce(sum(q.pivot_amount) FILTER (WHERE o.created_at >= @month_since), 0)
              FROM gateway.payment_order o
              JOIN gateway.quote q USING (quote_id)
             WHERE o.client_id = @client
               AND o.customer_id = @customer
               AND o.order_id <> @exclude
               AND o.status <> ALL(@uncounted)
               AND q.pivot_asset_key = @pivot
               AND o.created_at >= @month_since
            """,
            connection);
        command
            .With("day_since", daySince.Utc())
            .With("month_since", monthSince.Utc())
            .With("client", clientId)
            .With("customer", customerId)
            .With("exclude", excludeOrderId)
            .With("uncounted", new[] { DbNames.Of(OrderStatus.Expired), DbNames.Of(OrderStatus.Refunded) })
            .With("pivot", pivotCurrency.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new CustomerVolume(reader.GetDecimal(0), reader.GetDecimal(1));
    }

    private static NpgsqlCommand BindState(NpgsqlCommand command, PaymentOrder order) =>
        command
            .With("id", order.OrderId)
            .With("status", DbNames.Of(order.Status))
            .With("session", order.FiatSessionId)
            .With("checkout", order.CheckoutUrl?.ToString())
            .With("fiat_provider", order.FiatPayment?.Provider)
            .With("fiat_payment_id", order.FiatPayment?.ProviderPaymentId)
            .With("transaction_hash", order.Transfer?.TransactionHash)
            .With("confirmations", order.Confirmations)
            .With("updated_at", order.UpdatedAt.Utc())
            .WithJson("fiat_payment", RecordMapper.Serialize(order.FiatPayment))
            .WithJson("purchase", RecordMapper.Serialize(order.Purchase))
            .WithJson("transfer", RecordMapper.Serialize(order.Transfer))
            .WithJson("refund", RecordMapper.Serialize(order.Refund));

    private static async Task InsertHistoryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PaymentOrder order,
        int storedEvents,
        int storedDecisions,
        int storedReviews,
        CancellationToken cancellationToken)
    {
        foreach (var orderEvent in order.Events.Where(e => e.Sequence > storedEvents))
        {
            await using var command = new NpgsqlCommand(
                """
                INSERT INTO gateway.order_event (order_id, sequence, from_status, to_status, occurred_at, reason)
                VALUES (@order_id, @sequence, @from_status, @to_status, @occurred_at, @reason)
                """,
                connection,
                transaction);
            command
                .With("order_id", order.OrderId)
                .With("sequence", orderEvent.Sequence)
                .With("from_status", orderEvent.From is { } from ? DbNames.Of(from) : null)
                .With("to_status", DbNames.Of(orderEvent.To))
                .With("occurred_at", orderEvent.OccurredAt.Utc())
                .With("reason", orderEvent.Reason);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        for (var index = storedDecisions; index < order.ComplianceHistory.Count; index++)
        {
            var decision = order.ComplianceHistory[index];
            await using var command = new NpgsqlCommand(
                """
                INSERT INTO gateway.compliance_decision (order_id, sequence, outcome, reasons, screener, decided_at)
                VALUES (@order_id, @sequence, @outcome, @reasons, @screener, @decided_at)
                """,
                connection,
                transaction);
            command
                .With("order_id", order.OrderId)
                .With("sequence", index + 1)
                .With("outcome", DbNames.Of(decision.Outcome))
                .WithJson("reasons", RecordMapper.Serialize(decision.Reasons))
                .With("screener", decision.Screener)
                .With("decided_at", decision.DecidedAt.Utc());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var review in order.ReviewActions.Where(action => action.Sequence > storedReviews))
        {
            await using var command = new NpgsqlCommand(
                """
                INSERT INTO gateway.review_action (order_id, sequence, action, reviewer, reason, report_reference, occurred_at)
                VALUES (@order_id, @sequence, @action, @reviewer, @reason, @report_reference, @occurred_at)
                """,
                connection,
                transaction);
            command
                .With("order_id", order.OrderId)
                .With("sequence", review.Sequence)
                .With("action", DbNames.Of(review.Kind))
                .With("reviewer", review.Reviewer)
                .With("reason", review.Reason)
                .With("report_reference", review.ReportReference)
                .With("occurred_at", review.OccurredAt.Utc());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}

public sealed class PostgresLedgerStore(NpgsqlDataSource dataSource, AssetCatalog catalog) : ILedgerStore
{
    private readonly RecordMapper _mapper = new(catalog);

    public async Task AppendAsync(JournalEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LedgerRows.InsertAsync(connection, transaction, [entry], cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<JournalEntry>> ListAsync(Guid? orderId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT e.entry_id, e.kind, e.order_id, e.posted_at, l.account_code, l.asset_key, l.side, trim_scale(l.amount),
                   e.external_provider, e.external_id, e.external_asset_key, trim_scale(e.external_amount), e.external_occurred_at
              FROM ledger.journal_entry e
              JOIN ledger.journal_line l USING (entry_id)
             WHERE @order_id::uuid IS NULL OR e.order_id = @order_id::uuid
             ORDER BY e.entry_number, l.line_number
            """,
            connection);
        command.With("order_id", orderId);
        return await ReadEntriesAsync(command, cancellationToken);
    }

    public async Task<IReadOnlyList<JournalEntry>> ListForReconciliationAsync(
        string provider,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        IReadOnlyCollection<string> references,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(references);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT e.entry_id, e.kind, e.order_id, e.posted_at, l.account_code, l.asset_key, l.side, trim_scale(l.amount),
                   e.external_provider, e.external_id, e.external_asset_key, trim_scale(e.external_amount), e.external_occurred_at
              FROM ledger.journal_entry e
              JOIN ledger.journal_line l USING (entry_id)
             WHERE e.external_provider = @provider
               AND ((e.external_occurred_at >= @window_start AND e.external_occurred_at < @window_end)
                    OR e.external_id = ANY(@references))
             ORDER BY e.entry_number, l.line_number
            """,
            connection);
        command
            .With("provider", provider)
            .With("window_start", windowStart.Utc())
            .With("window_end", windowEnd.Utc())
            .With("references", references.ToArray());
        return await ReadEntriesAsync(command, cancellationToken);
    }

    /// <summary>Reads rows of entry columns 0-3, line columns 4-7 and provider-reference columns 8-12, ordered by entry.</summary>
    private async Task<IReadOnlyList<JournalEntry>> ReadEntriesAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var entries = new List<JournalEntry>();
        var lines = new List<JournalLine>();
        (Guid Id, string Kind, Guid? OrderId, DateTimeOffset PostedAt, ExternalReference? External)? current = null;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var entryId = reader.GetGuid(0);
            if (current is { } open && open.Id != entryId)
            {
                entries.Add(new JournalEntry(open.Id, open.Kind, open.OrderId, open.PostedAt, lines, open.External));
                lines = [];
            }

            var external = reader.IsDBNull(8)
                ? null
                : new ExternalReference(
                    reader.GetString(8),
                    reader.GetString(9),
                    _mapper.Money(reader.GetDecimal(11), reader.GetString(10)),
                    reader.GetFieldValue<DateTimeOffset>(12));
            current = (entryId, reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetGuid(2), reader.GetFieldValue<DateTimeOffset>(3), external);
            var assetKey = reader.GetString(5);
            lines.Add(new JournalLine(
                new LedgerAccount(reader.GetString(4), AssetId.Parse(assetKey)),
                DbNames.Parse<EntrySide>(reader.GetString(6)),
                _mapper.Money(reader.GetDecimal(7), assetKey)));
        }

        if (current is { } last)
        {
            entries.Add(new JournalEntry(last.Id, last.Kind, last.OrderId, last.PostedAt, lines, last.External));
        }

        return entries.AsReadOnly();
    }
}

public sealed class PostgresIdempotencyStore(NpgsqlDataSource dataSource) : IIdempotencyStore
{
    public async Task<IdempotencyRecord?> TryClaimAsync(string clientId, string key, string requestHash, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using (var claim = new NpgsqlCommand(
            """
            INSERT INTO gateway.idempotency_key (client_id, idempotency_key, request_sha256)
            VALUES (@client, @key, @hash)
            ON CONFLICT (client_id, idempotency_key) DO NOTHING
            """,
            connection))
        {
            claim.With("client", clientId).With("key", key).With("hash", requestHash);
            if (await claim.ExecuteNonQueryAsync(cancellationToken) == 1)
            {
                return null;
            }
        }

        await using var existing = new NpgsqlCommand(
            "SELECT request_sha256, response_status, response_body FROM gateway.idempotency_key WHERE client_id = @client AND idempotency_key = @key",
            connection);
        existing.With("client", clientId).With("key", key);
        await using var reader = await existing.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            // Released between the insert and this read; the caller may retry.
            return new IdempotencyRecord(string.Empty, null);
        }

        var response = reader.IsDBNull(1) ? null : new StoredResponse(reader.GetInt16(1), reader.GetString(2));
        return new IdempotencyRecord(reader.GetString(0), response);
    }

    public async Task CompleteAsync(string clientId, string key, StoredResponse response, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            UPDATE gateway.idempotency_key
               SET response_status = @status, response_body = @body, completed_at = now()
             WHERE client_id = @client AND idempotency_key = @key AND completed_at IS NULL
            """,
            connection);
        command.With("status", (short)response.StatusCode).With("body", response.Body).With("client", clientId).With("key", key);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("Complete was called for an unclaimed or already completed idempotency key.");
        }
    }

    public async Task ReleaseAsync(string clientId, string key, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "DELETE FROM gateway.idempotency_key WHERE client_id = @client AND idempotency_key = @key AND completed_at IS NULL",
            connection);
        command.With("client", clientId).With("key", key);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

public sealed class PostgresWebhookInbox(NpgsqlDataSource dataSource) : IWebhookInbox
{
    public async Task<bool> TryClaimAsync(string provider, string eventId, string payloadSha256, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO ops.webhook_inbox (provider, event_id, payload_sha256)
            VALUES (@provider, @event_id, @hash)
            ON CONFLICT (provider, event_id) DO NOTHING
            """,
            connection);
        command.With("provider", provider).With("event_id", eventId).With("hash", payloadSha256);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task ReleaseAsync(string provider, string eventId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "DELETE FROM ops.webhook_inbox WHERE provider = @provider AND event_id = @event_id",
            connection);
        command.With("provider", provider).With("event_id", eventId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

internal static class OutboxRows
{
    public static async Task InsertAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IEnumerable<OutboxMessage> messages,
        CancellationToken cancellationToken)
    {
        foreach (var message in messages)
        {
            await using var command = new NpgsqlCommand(
                """
                INSERT INTO ops.outbox_message (
                    message_id, aggregate_type, aggregate_id, client_id, event_sequence, event_type, payload, occurred_at, next_attempt_at)
                VALUES (@id, @aggregate_type, @aggregate_id, @client, @sequence, @type, @payload, @occurred_at, @occurred_at)
                """,
                connection,
                transaction);
            command
                .With("id", message.MessageId)
                .With("aggregate_type", message.AggregateType)
                .With("aggregate_id", message.AggregateId)
                .With("client", message.ClientId)
                .With("sequence", message.Sequence)
                .With("type", message.EventType)
                .WithJson("payload", message.Payload)
                .With("occurred_at", message.OccurredAt.Utc());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}

/// <summary>
/// Claims use <c>FOR UPDATE SKIP LOCKED</c> and a lease, so two instances never send the same message at once;
/// each order's messages go out in sequence.
/// </summary>
public sealed class PostgresOutbox(NpgsqlDataSource dataSource) : IOutbox
{
    public async Task<IReadOnlyList<OutboxMessage>> ClaimDueAsync(DateTimeOffset now, TimeSpan lease, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            WITH next_per_order AS (
                SELECT DISTINCT ON (aggregate_id) message_id
                  FROM ops.outbox_message
                 WHERE published_at IS NULL AND dead_at IS NULL
                 ORDER BY aggregate_id, event_sequence
            ), due AS (
                SELECT m.message_id
                  FROM ops.outbox_message m
                  JOIN next_per_order n USING (message_id)
                 WHERE m.next_attempt_at <= @now AND m.published_at IS NULL AND m.dead_at IS NULL -- rechecked once locked
                 ORDER BY m.next_attempt_at
                 LIMIT @limit
                   FOR UPDATE OF m SKIP LOCKED
            )
            UPDATE ops.outbox_message o
               SET next_attempt_at = @lease_until
              FROM due
             WHERE o.message_id = due.message_id
            RETURNING o.message_id, o.client_id, o.aggregate_type, o.aggregate_id, o.event_sequence, o.event_type, o.payload::text, o.attempts, o.occurred_at
            """,
            connection);
        command.With("now", now.Utc()).With("limit", limit).With("lease_until", (now + lease).Utc());
        var messages = new List<OutboxMessage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            messages.Add(new OutboxMessage(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetGuid(3),
                reader.GetInt32(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetInt32(7),
                reader.GetFieldValue<DateTimeOffset>(8)));
        }

        return messages.AsReadOnly();
    }

    public Task MarkDeliveredAsync(Guid messageId, DateTimeOffset at, CancellationToken cancellationToken) =>
        ExecuteAsync(
            "UPDATE ops.outbox_message SET attempts = attempts + 1, published_at = @at WHERE message_id = @id",
            command => command.With("at", at.Utc()).With("id", messageId),
            cancellationToken);

    public Task ScheduleRetryAsync(Guid messageId, DateTimeOffset nextAttemptAt, string failure, CancellationToken cancellationToken) =>
        ExecuteAsync(
            "UPDATE ops.outbox_message SET attempts = attempts + 1, next_attempt_at = @next, last_error = @failure WHERE message_id = @id",
            command => command.With("next", nextAttemptAt.Utc()).With("failure", failure).With("id", messageId),
            cancellationToken);

    public Task MarkDeadAsync(Guid messageId, string failure, DateTimeOffset at, CancellationToken cancellationToken) =>
        ExecuteAsync(
            "UPDATE ops.outbox_message SET attempts = attempts + 1, dead_at = @at, last_error = @failure WHERE message_id = @id",
            command => command.With("at", at.Utc()).With("failure", failure).With("id", messageId),
            cancellationToken);

    public async Task<IReadOnlyList<FailedDelivery>> ListFailedAsync(DateTimeOffset since, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT message_id, client_id, aggregate_type, aggregate_id, event_sequence, event_type, attempts, last_error, dead_at
              FROM ops.outbox_message
             WHERE dead_at >= @since AND last_error IS DISTINCT FROM @no_webhook
             ORDER BY dead_at DESC
             LIMIT @limit
            """,
            connection);
        command.With("since", since.Utc()).With("no_webhook", IOutbox.NoWebhookConfigured).With("limit", limit);
        var failed = new List<FailedDelivery>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            failed.Add(new FailedDelivery(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetGuid(3),
                reader.GetInt32(4),
                reader.GetString(5),
                reader.GetInt32(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.GetFieldValue<DateTimeOffset>(8)));
        }

        return failed.AsReadOnly();
    }

    public async Task<bool> RetryAsync(Guid messageId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            UPDATE ops.outbox_message SET dead_at = NULL, next_attempt_at = @now
             WHERE message_id = @id AND dead_at IS NOT NULL AND published_at IS NULL
            """,
            connection);
        command.With("now", now.Utc()).With("id", messageId);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<IReadOnlyList<OutboxMessageStatus>> ListForAggregateAsync(Guid aggregateId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT message_id, event_sequence, attempts, published_at, dead_at, last_error
              FROM ops.outbox_message
             WHERE aggregate_id = @aggregate_id
             ORDER BY event_sequence
            """,
            connection);
        command.With("aggregate_id", aggregateId);
        var statuses = new List<OutboxMessageStatus>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            statuses.Add(new OutboxMessageStatus(
                reader.GetGuid(0),
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
                reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return statuses.AsReadOnly();
    }

    private async Task ExecuteAsync(string sql, Action<NpgsqlCommand> bind, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        bind(command);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

/// <summary>Stores runs and their discrepancies in one transaction; both tables are append-only.</summary>
public sealed class PostgresReconciliationStore(NpgsqlDataSource dataSource, AssetCatalog catalog) : IReconciliationStore
{
    private readonly RecordMapper _mapper = new(catalog);

    public async Task SaveAsync(ReconciliationRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var command = new NpgsqlCommand(
            """
            INSERT INTO ops.reconciliation_run (
                run_id, provider, business_day, window_start, window_end, ran_at, report_lines, ledger_entries, matched, discrepancies)
            VALUES (@id, @provider, @day, @start, @end, @ran_at, @report_lines, @ledger_entries, @matched, @discrepancies)
            """,
            connection,
            transaction))
        {
            command
                .With("id", run.RunId)
                .With("provider", run.Provider)
                .With("day", run.Day)
                .With("start", run.WindowStart.Utc())
                .With("end", run.WindowEnd.Utc())
                .With("ran_at", run.RanAt.Utc())
                .With("report_lines", run.ReportLines)
                .With("ledger_entries", run.LedgerEntries)
                .With("matched", run.Matched)
                .With("discrepancies", run.Discrepancies.Count);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        for (var index = 0; index < run.Discrepancies.Count; index++)
        {
            var discrepancy = run.Discrepancies[index];
            await using var command = new NpgsqlCommand(
                """
                INSERT INTO ops.reconciliation_discrepancy (
                    run_id, line_number, discrepancy_type, kind, reference, order_id,
                    report_asset_key, report_amount, ledger_asset_key, ledger_amount)
                VALUES (@run_id, @line_number, @type, @kind, @reference, @order_id,
                        @report_asset, @report_amount, @ledger_asset, @ledger_amount)
                """,
                connection,
                transaction);
            command
                .With("run_id", run.RunId)
                .With("line_number", index + 1)
                .With("type", DbNames.Of(discrepancy.Type))
                .With("kind", discrepancy.Kind)
                .With("reference", discrepancy.Reference)
                .With("order_id", discrepancy.OrderId)
                .With("report_asset", discrepancy.ReportAmount?.Asset.Id.ToString())
                .With("report_amount", discrepancy.ReportAmount?.Amount)
                .With("ledger_asset", discrepancy.LedgerAmount?.Asset.Id.ToString())
                .With("ledger_amount", discrepancy.LedgerAmount?.Amount);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ReconciliationRun>> ListAsync(DateOnly? day, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var runs = new List<(Guid Id, string Provider, DateOnly Day, DateTimeOffset Start, DateTimeOffset End, DateTimeOffset RanAt, int Lines, int Entries, int Matched)>();
        await using (var command = new NpgsqlCommand(
            """
            SELECT run_id, provider, business_day, window_start, window_end, ran_at, report_lines, ledger_entries, matched
              FROM ops.reconciliation_run
             WHERE @day::date IS NULL OR business_day = @day::date
             ORDER BY ran_at DESC, run_number DESC
             LIMIT @limit
            """,
            connection))
        {
            command.With("day", day).With("limit", limit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                runs.Add((
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.GetFieldValue<DateOnly>(2),
                    reader.GetFieldValue<DateTimeOffset>(3),
                    reader.GetFieldValue<DateTimeOffset>(4),
                    reader.GetFieldValue<DateTimeOffset>(5),
                    reader.GetInt32(6),
                    reader.GetInt32(7),
                    reader.GetInt32(8)));
            }
        }

        var discrepancies = runs.ToDictionary(run => run.Id, _ => new List<Discrepancy>());
        if (runs.Count > 0)
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT run_id, discrepancy_type, kind, reference, order_id,
                       report_asset_key, trim_scale(report_amount), ledger_asset_key, trim_scale(ledger_amount)
                  FROM ops.reconciliation_discrepancy
                 WHERE run_id = ANY(@ids)
                 ORDER BY run_id, line_number
                """,
                connection);
            command.With("ids", runs.Select(run => run.Id).ToArray());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                discrepancies[reader.GetGuid(0)].Add(new Discrepancy(
                    DbNames.Parse<DiscrepancyType>(reader.GetString(1)),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetGuid(4),
                    reader.IsDBNull(5) ? null : _mapper.Money(reader.GetDecimal(6), reader.GetString(5)),
                    reader.IsDBNull(7) ? null : _mapper.Money(reader.GetDecimal(8), reader.GetString(7))));
            }
        }

        return runs
            .Select(run => new ReconciliationRun(
                run.Id, run.Provider, run.Day, run.Start, run.End, run.RanAt, run.Lines, run.Entries, run.Matched, discrepancies[run.Id].AsReadOnly()))
            .ToList()
            .AsReadOnly();
    }
}
