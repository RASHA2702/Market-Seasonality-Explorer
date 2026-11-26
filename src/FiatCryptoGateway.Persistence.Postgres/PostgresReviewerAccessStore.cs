using FiatCryptoGateway.Application.Reviewers;
using Npgsql;

namespace FiatCryptoGateway.Persistence.Postgres;

/// <summary>
/// Reviewer sign-in state in PostgreSQL. Counting failures, using an authenticator step and opening a session are each
/// one statement or one transaction, so instances never double-count a failure or accept the same code twice.
/// </summary>
public sealed class PostgresReviewerAccessStore(NpgsqlDataSource dataSource) : IReviewerAccessStore
{
    public async Task<ReviewerGuard> GetGuardAsync(string username, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT failed_attempts, locked_until, last_totp_step FROM ops.reviewer_guard WHERE username = @username");
        command.With("username", username);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new ReviewerGuard(
                reader.GetInt32(0),
                await reader.IsDBNullAsync(1, cancellationToken) ? null : reader.GetFieldValue<DateTimeOffset>(1),
                reader.GetInt64(2))
            : ReviewerGuard.Fresh;
    }

    public async Task<bool> RecordFailureAsync(string username, DateTimeOffset now, int maxFailedAttempts, DateTimeOffset lockedUntil, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        bool locks;

        // The row lock taken by the upsert serialises concurrent failures. A failure that reaches the limit starts a
        // pause and resets the count, so after it the count is 0; any other failure leaves it at 1 or more.
        await using (var count = new NpgsqlCommand(
            """
            INSERT INTO ops.reviewer_guard AS guard (username, failed_attempts, locked_until)
            VALUES (@username, CASE WHEN @max <= 1 THEN 0 ELSE 1 END, CASE WHEN @max <= 1 THEN @locked_until END)
            ON CONFLICT (username) DO UPDATE SET
                failed_attempts = CASE WHEN guard.failed_attempts + 1 >= @max THEN 0 ELSE guard.failed_attempts + 1 END,
                locked_until = CASE WHEN guard.failed_attempts + 1 >= @max THEN @locked_until ELSE guard.locked_until END
            RETURNING failed_attempts = 0
            """,
            connection,
            transaction))
        {
            count.With("username", username).With("max", maxFailedAttempts).With("locked_until", lockedUntil.Utc());
            locks = (bool)(await count.ExecuteScalarAsync(cancellationToken))!;
        }

        await LogAsync(connection, transaction, username, ReviewerAccessEventKind.SignInFailed, now, null, cancellationToken);
        if (locks)
        {
            await LogAsync(connection, transaction, username, ReviewerAccessEventKind.LockedOut, now, null, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return locks;
    }

    public async Task RecordBlockedAsync(string username, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await LogAsync(connection, null, username, ReviewerAccessEventKind.SignInBlocked, now, null, cancellationToken);
    }

    public async Task<bool> OpenSessionAsync(ReviewerSession session, long totpStep, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var useStep = new NpgsqlCommand(
            """
            INSERT INTO ops.reviewer_guard AS guard (username, failed_attempts, locked_until, last_totp_step)
            VALUES (@username, 0, NULL, @step)
            ON CONFLICT (username) DO UPDATE SET failed_attempts = 0, locked_until = NULL, last_totp_step = @step
                WHERE guard.last_totp_step < @step AND (guard.locked_until IS NULL OR guard.locked_until <= @now)
            RETURNING 1
            """,
            connection,
            transaction))
        {
            useStep.With("username", session.Username).With("step", totpStep).With("now", session.CreatedAt.Utc());
            if (await useStep.ExecuteScalarAsync(cancellationToken) is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }
        }

        await using (var open = new NpgsqlCommand(
            """
            INSERT INTO ops.reviewer_session (
                session_id, token_sha256, username, credential_fingerprint, created_at, last_seen_at, expires_at)
            VALUES (@id, @token, @username, @fingerprint, @created, @created, @expires)
            """,
            connection,
            transaction))
        {
            await open
                .With("id", session.SessionId)
                .With("token", session.TokenSha256)
                .With("username", session.Username)
                .With("fingerprint", session.CredentialFingerprint)
                .With("created", session.CreatedAt.Utc())
                .With("expires", session.ExpiresAt.Utc())
                .ExecuteNonQueryAsync(cancellationToken);
        }

        await LogAsync(connection, transaction, session.Username, ReviewerAccessEventKind.SignedIn, session.CreatedAt, session.SessionId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<ReviewerSession?> FindSessionAsync(byte[] tokenSha256, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT session_id, token_sha256, username, credential_fingerprint, created_at, last_seen_at, expires_at, ended_at
              FROM ops.reviewer_session
             WHERE token_sha256 = @token
            """);
        command.With("token", tokenSha256);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ReviewerSession(
            reader.GetGuid(0),
            reader.GetFieldValue<byte[]>(1),
            reader.GetString(2),
            reader.GetFieldValue<byte[]>(3),
            reader.GetFieldValue<DateTimeOffset>(4),
            reader.GetFieldValue<DateTimeOffset>(5),
            reader.GetFieldValue<DateTimeOffset>(6),
            await reader.IsDBNullAsync(7, cancellationToken) ? null : reader.GetFieldValue<DateTimeOffset>(7));
    }

    public async Task TouchSessionAsync(Guid sessionId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "UPDATE ops.reviewer_session SET last_seen_at = @now WHERE session_id = @id AND ended_at IS NULL AND last_seen_at < @now");
        await command.With("id", sessionId).With("now", now.Utc()).ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> EndSessionAsync(Guid sessionId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        string username;
        await using (var end = new NpgsqlCommand(
            "UPDATE ops.reviewer_session SET ended_at = @now WHERE session_id = @id AND ended_at IS NULL RETURNING username",
            connection,
            transaction))
        {
            end.With("id", sessionId).With("now", now.Utc());
            if (await end.ExecuteScalarAsync(cancellationToken) is not string ended)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }

            username = ended;
        }

        await LogAsync(connection, transaction, username, ReviewerAccessEventKind.SignedOut, now, sessionId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<ReviewerAccessEvent>> ListEventsAsync(string username, int limit, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT kind, occurred_at, session_id FROM ops.reviewer_access_event
             WHERE username = @username ORDER BY event_id DESC LIMIT @limit
            """);
        command.With("username", username).With("limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var events = new List<ReviewerAccessEvent>();
        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(new ReviewerAccessEvent(
                username,
                DbNames.Parse<ReviewerAccessEventKind>(reader.GetString(0)),
                reader.GetFieldValue<DateTimeOffset>(1),
                await reader.IsDBNullAsync(2, cancellationToken) ? null : reader.GetGuid(2)));
        }

        return events;
    }

    private static async Task LogAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string username,
        ReviewerAccessEventKind kind,
        DateTimeOffset at,
        Guid? sessionId,
        CancellationToken cancellationToken)
    {
        await using var log = new NpgsqlCommand(
            "INSERT INTO ops.reviewer_access_event (username, kind, occurred_at, session_id) VALUES (@username, @kind, @at, @session)",
            connection,
            transaction);
        await log
            .With("username", username)
            .With("kind", DbNames.Of(kind))
            .With("at", at.Utc())
            .With("session", sessionId)
            .ExecuteNonQueryAsync(cancellationToken);
    }
}
