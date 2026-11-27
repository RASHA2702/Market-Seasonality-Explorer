using FiatCryptoGateway.Application.Payments;
using FiatCryptoGateway.Domain.Assets;
using FiatCryptoGateway.Domain.Payments;
using Npgsql;

namespace FiatCryptoGateway.Persistence.Postgres;

/// <summary>
/// The processor's records in PostgreSQL. An attempt and its new history are written in one transaction, a save is
/// refused if the attempt changed since it was loaded, and the database itself refuses a second captured payment for an
/// order, an illegal step, a change to what an attempt is for, and any text that looks like a card number.
/// </summary>
public sealed class PostgresPaymentAttemptStore(NpgsqlDataSource dataSource, AssetCatalog catalog) : IPaymentAttemptStore
{
    private const string UniqueViolation = "23505";
    private const string OneCaptureIndex = "ux_payment_attempt_one_capture";

    private const string Columns =
        "attempt_id, order_id, client_id, method, amount_asset_key, amount, status, instrument_label, instrument_brand, " +
        "instrument_last4, rail_reference, challenge_kind, challenge_max_tries, challenge_tries_used, decline_code, created_at, " +
        "updated_at, expires_at, captured_at, provider_payment_id, recorded_on_order_at, refund_id, refunded_at, row_version";

    private readonly RecordMapper _mapper = new(catalog);

    public async Task AddAsync(PaymentAttempt attempt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using (var insert = new NpgsqlCommand(
                $"""
                INSERT INTO gateway.payment_attempt ({Columns})
                VALUES (@attempt, @order, @client, @method, @asset, @amount, @status, @label, @brand, @last4, @rail, @kind,
                        @max_tries, @tries_used, @decline, @created, @updated, @expires, @captured, @payment, @recorded,
                        @refund, @refunded, 1)
                """,
                connection,
                transaction))
            {
                await Bind(insert, attempt).ExecuteNonQueryAsync(cancellationToken);
            }

            await InsertEventsAsync(connection, transaction, attempt, afterSequence: 0, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == UniqueViolation)
        {
            throw Conflict(exception);
        }

        attempt.Version = 1;
    }

    public async Task SaveAsync(PaymentAttempt attempt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        int newVersion;
        try
        {
            await using (var update = new NpgsqlCommand(
                """
                UPDATE gateway.payment_attempt
                   SET status = @status, rail_reference = @rail, challenge_kind = @kind, challenge_max_tries = @max_tries,
                       challenge_tries_used = @tries_used, decline_code = @decline, updated_at = @updated, captured_at = @captured,
                       provider_payment_id = @payment, recorded_on_order_at = @recorded, refund_id = @refund,
                       refunded_at = @refunded, row_version = row_version + 1
                 WHERE attempt_id = @attempt AND row_version = @version
                 RETURNING row_version
                """,
                connection,
                transaction))
            {
                Bind(update, attempt).With("version", attempt.Version);
                if (await update.ExecuteScalarAsync(cancellationToken) is not int version)
                {
                    throw new PaymentAttemptConcurrencyException();
                }

                newVersion = version;
            }

            int stored;
            await using (var count = new NpgsqlCommand(
                "SELECT coalesce(max(sequence), 0) FROM gateway.payment_attempt_event WHERE attempt_id = @attempt", connection, transaction))
            {
                count.With("attempt", attempt.AttemptId);
                stored = (int)(await count.ExecuteScalarAsync(cancellationToken))!;
            }

            await InsertEventsAsync(connection, transaction, attempt, stored, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == UniqueViolation)
        {
            throw Conflict(exception);
        }

        attempt.Version = newVersion;
    }

    /// <summary>
    /// A second captured payment for an order is a lost race, which callers expect and handle. Any other reused value (an
    /// attempt, payment or refund identifier) is a programming error: it is refused loudly and never mistaken for a race.
    /// </summary>
    private static Exception Conflict(PostgresException exception) =>
        exception.ConstraintName == OneCaptureIndex
            ? new PaymentAttemptConcurrencyException("The order already has a captured payment.", exception)
            : new InvalidOperationException("An attempt, payment or refund identifier was already used by another payment attempt.", exception);

    public async Task<PaymentAttempt?> FindAsync(Guid attemptId, CancellationToken cancellationToken)
    {
        var found = await QueryAsync("WHERE attempt_id = @id", "attempt_id", int.MaxValue, command => command.With("id", attemptId), cancellationToken);
        return found.Count == 0 ? null : found[0];
    }

    public Task<IReadOnlyList<PaymentAttempt>> ListForOrderAsync(Guid orderId, CancellationToken cancellationToken) =>
        QueryAsync("WHERE order_id = @order", "created_at, attempt_id", int.MaxValue, command => command.With("order", orderId), cancellationToken);

    public async Task<PaymentAttempt?> FindCapturedForOrderAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var found = await QueryAsync(
            "WHERE order_id = @order AND status IN ('CAPTURED', 'REFUNDED')", "attempt_id", 1, command => command.With("order", orderId), cancellationToken);
        return found.Count == 0 ? null : found[0];
    }

    public Task<IReadOnlyList<PaymentAttempt>> ListCapturedNotRecordedAsync(int limit, CancellationToken cancellationToken) =>
        QueryAsync(
            "WHERE status IN ('CAPTURED', 'REFUNDED') AND recorded_on_order_at IS NULL", "captured_at, attempt_id", limit, _ => { }, cancellationToken);

    public Task<IReadOnlyList<PaymentAttempt>> ListStuckAuthorizedAsync(DateTimeOffset cutoff, int limit, CancellationToken cancellationToken) =>
        QueryAsync(
            "WHERE status = 'AUTHORIZED' AND updated_at <= @cutoff", "updated_at, attempt_id", limit, command => command.With("cutoff", cutoff.Utc()), cancellationToken);

    public Task<IReadOnlyList<PaymentAttempt>> ListPendingExpiredAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken) =>
        QueryAsync(
            "WHERE status IN ('STARTED', 'AWAITING_AUTHENTICATION') AND expires_at <= @now",
            "expires_at, attempt_id",
            limit,
            command => command.With("now", now.Utc()),
            cancellationToken);

    public Task<IReadOnlyList<PaymentAttempt>> ListSettledBetweenAsync(DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken) =>
        QueryAsync(
            "WHERE (captured_at >= @start AND captured_at < @end) OR (refunded_at >= @start AND refunded_at < @end)",
            "coalesce(captured_at, created_at), attempt_id",
            int.MaxValue,
            command => command.With("start", windowStart.Utc()).With("end", windowEnd.Utc()),
            cancellationToken);

    private static NpgsqlCommand Bind(NpgsqlCommand command, PaymentAttempt attempt) =>
        command
            .With("attempt", attempt.AttemptId)
            .With("order", attempt.OrderId)
            .With("client", attempt.ClientId)
            .With("method", DbNames.Of(attempt.Method))
            .With("asset", attempt.Amount.Asset.Id.ToString())
            .With("amount", attempt.Amount.Amount)
            .With("status", DbNames.Of(attempt.Status))
            .With("label", attempt.Instrument.Label)
            .With("brand", attempt.Instrument.Brand)
            .With("last4", attempt.Instrument.Last4)
            .With("rail", attempt.RailReference)
            .With("kind", attempt.Challenge is { } challenge ? DbNames.Of(challenge.Kind) : null)
            .With("max_tries", attempt.Challenge?.MaxTries)
            .With("tries_used", attempt.Challenge?.TriesUsed)
            .With("decline", attempt.DeclineCode)
            .With("created", attempt.CreatedAt.Utc())
            .With("updated", attempt.UpdatedAt.Utc())
            .With("expires", attempt.ExpiresAt.Utc())
            .With("captured", attempt.CapturedAt?.Utc())
            .With("payment", attempt.ProviderPaymentId)
            .With("recorded", attempt.RecordedOnOrderAt?.Utc())
            .With("refund", attempt.RefundId)
            .With("refunded", attempt.RefundedAt?.Utc());

    private static async Task InsertEventsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, PaymentAttempt attempt, int afterSequence, CancellationToken cancellationToken)
    {
        foreach (var entry in attempt.Events.Where(entry => entry.Sequence > afterSequence))
        {
            await using var insert = new NpgsqlCommand(
                """
                INSERT INTO gateway.payment_attempt_event (attempt_id, sequence, from_status, to_status, occurred_at, reason)
                VALUES (@attempt, @sequence, @from, @to, @at, @reason)
                """,
                connection,
                transaction);
            await insert
                .With("attempt", attempt.AttemptId)
                .With("sequence", entry.Sequence)
                .With("from", entry.From is { } from ? DbNames.Of(from) : null)
                .With("to", DbNames.Of(entry.To))
                .With("at", entry.OccurredAt.Utc())
                .With("reason", entry.Reason)
                .ExecuteNonQueryAsync(cancellationToken);
        }
    }

    // The clauses are this class's own constants, never input; every value goes in as a parameter.
    private async Task<IReadOnlyList<PaymentAttempt>> QueryAsync(
        string where, string orderBy, int limit, Action<NpgsqlCommand> parameters, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = new List<Row>();
        await using (var command = new NpgsqlCommand($"SELECT {Columns} FROM gateway.payment_attempt {where} ORDER BY {orderBy} LIMIT @limit", connection))
        {
            parameters(command);
            command.With("limit", limit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(await ReadRowAsync(reader, cancellationToken));
            }
        }

        if (rows.Count == 0)
        {
            return [];
        }

        var events = new Dictionary<Guid, List<AttemptEvent>>();
        await using (var command = new NpgsqlCommand(
            "SELECT attempt_id, sequence, from_status, to_status, occurred_at, reason FROM gateway.payment_attempt_event WHERE attempt_id = ANY(@ids) ORDER BY attempt_id, sequence",
            connection))
        {
            command.With("ids", rows.Select(row => row.AttemptId).ToArray());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var attemptId = reader.GetGuid(0);
                if (!events.TryGetValue(attemptId, out var list))
                {
                    events[attemptId] = list = [];
                }

                list.Add(new AttemptEvent(
                    reader.GetInt32(1),
                    await reader.IsDBNullAsync(2, cancellationToken) ? null : DbNames.Parse<PaymentAttemptStatus>(reader.GetString(2)),
                    DbNames.Parse<PaymentAttemptStatus>(reader.GetString(3)),
                    reader.GetFieldValue<DateTimeOffset>(4),
                    reader.GetString(5)));
            }
        }

        return [.. rows.Select(row => Restore(row, events.GetValueOrDefault(row.AttemptId) ?? []))];
    }

    private static async Task<Row> ReadRowAsync(NpgsqlDataReader reader, CancellationToken cancellationToken)
    {
        async Task<string?> Text(int ordinal) => await reader.IsDBNullAsync(ordinal, cancellationToken) ? null : reader.GetString(ordinal);
        async Task<int?> Number(int ordinal) => await reader.IsDBNullAsync(ordinal, cancellationToken) ? null : reader.GetInt32(ordinal);
        async Task<DateTimeOffset?> Time(int ordinal) => await reader.IsDBNullAsync(ordinal, cancellationToken) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);

        return new Row(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetString(2),
            DbNames.Parse<PaymentMethod>(reader.GetString(3)),
            reader.GetString(4),
            reader.GetDecimal(5),
            DbNames.Parse<PaymentAttemptStatus>(reader.GetString(6)),
            reader.GetString(7),
            await Text(8),
            await Text(9),
            await Text(10),
            await Text(11),
            await Number(12),
            await Number(13),
            await Text(14),
            reader.GetFieldValue<DateTimeOffset>(15),
            reader.GetFieldValue<DateTimeOffset>(16),
            reader.GetFieldValue<DateTimeOffset>(17),
            await Time(18),
            await Text(19),
            await Time(20),
            await Text(21),
            await Time(22),
            reader.GetInt32(23));
    }

    private PaymentAttempt Restore(Row row, IEnumerable<AttemptEvent> events) =>
        PaymentAttempt.Restore(
            row.AttemptId,
            row.OrderId,
            row.ClientId,
            _mapper.Money(row.Amount, row.AssetKey),
            new PaymentInstrument(row.Method, row.Label, row.Brand, row.Last4),
            row.Status,
            row.RailReference,
            row.ChallengeKind is { } kind ? new AuthenticationChallenge(DbNames.Parse<ChallengeKind>(kind), row.MaxTries!.Value, row.TriesUsed!.Value) : null,
            row.DeclineCode,
            row.CreatedAt,
            row.UpdatedAt,
            row.ExpiresAt,
            row.CapturedAt,
            row.ProviderPaymentId,
            row.RecordedOnOrderAt,
            row.RefundId,
            row.RefundedAt,
            row.Version,
            events);

    private sealed record Row(
        Guid AttemptId,
        Guid OrderId,
        string ClientId,
        PaymentMethod Method,
        string AssetKey,
        decimal Amount,
        PaymentAttemptStatus Status,
        string Label,
        string? Brand,
        string? Last4,
        string? RailReference,
        string? ChallengeKind,
        int? MaxTries,
        int? TriesUsed,
        string? DeclineCode,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        DateTimeOffset ExpiresAt,
        DateTimeOffset? CapturedAt,
        string? ProviderPaymentId,
        DateTimeOffset? RecordedOnOrderAt,
        string? RefundId,
        DateTimeOffset? RefundedAt,
        int Version);
}
