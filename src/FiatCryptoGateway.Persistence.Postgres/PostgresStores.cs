using FiatCryptoGateway.Application.Ports;
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
            SELECT e.entry_id, e.kind, e.order_id, e.posted_at, l.account_code, l.asset_key, l.side, trim_scale(l.amount)
              FROM ledger.journal_entry e
              JOIN ledger.journal_line l USING (entry_id)
             WHERE @order_id::uuid IS NULL OR e.order_id = @order_id::uuid
             ORDER BY e.entry_number, l.line_number
            """,
            connection);
        command.With("order_id", orderId);

        var entries = new List<JournalEntry>();
        var lines = new List<JournalLine>();
        (Guid Id, string Kind, Guid? OrderId, DateTimeOffset PostedAt)? current = null;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var entryId = reader.GetGuid(0);
            if (current is { } open && open.Id != entryId)
            {
                entries.Add(new JournalEntry(open.Id, open.Kind, open.OrderId, open.PostedAt, lines));
                lines = [];
            }

            current = (entryId, reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetGuid(2), reader.GetFieldValue<DateTimeOffset>(3));
            var assetKey = reader.GetString(5);
            lines.Add(new JournalLine(
                new LedgerAccount(reader.GetString(4), AssetId.Parse(assetKey)),
                DbNames.Parse<EntrySide>(reader.GetString(6)),
                _mapper.Money(reader.GetDecimal(7), assetKey)));
        }

        if (current is { } last)
        {
            entries.Add(new JournalEntry(last.Id, last.Kind, last.OrderId, last.PostedAt, lines));
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
