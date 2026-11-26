using FiatCryptoGateway.Application.Ports;
using FiatCryptoGateway.Domain.Assets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace FiatCryptoGateway.Persistence.Postgres;

/// <summary>
/// A per-order PostgreSQL advisory lock, held on its own connection for the whole step (including the
/// provider call), so only one API instance can work on an order at a time. If the process dies, the
/// connection closes and PostgreSQL releases the lock.
/// </summary>
public sealed class PostgresOrderLocks(NpgsqlDataSource dataSource) : IOrderLocks
{
    // Namespace for order locks in PostgreSQL's two-key advisory-lock space.
    private const int OrderLockClass = 7001;

    public async Task<IAsyncDisposable> AcquireAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = new NpgsqlCommand("SELECT pg_advisory_lock(@class, @key)", connection);
            command.With("class", OrderLockClass).With("key", orderId.GetHashCode());
            await command.ExecuteNonQueryAsync(cancellationToken);
            return new Releaser(connection, orderId.GetHashCode());
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private sealed class Releaser(NpgsqlConnection connection, int key) : IAsyncDisposable
    {
        private int _released;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
            {
                return;
            }

            try
            {
                await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock(@class, @key)", connection);
                command.With("class", OrderLockClass).With("key", key);
                await command.ExecuteNonQueryAsync();
            }
            finally
            {
                // Returning the connection to the pool also resets its session, releasing any lock left behind.
                await connection.DisposeAsync();
            }
        }
    }
}

/// <summary>
/// Applies the embedded copies of db/migrations in order, once each, with the same rules as
/// db/migrate.sh. An advisory lock stops two instances migrating at the same time.
/// </summary>
public sealed partial class PostgresMigrator(NpgsqlDataSource dataSource, ILogger<PostgresMigrator> logger)
{
    private const long MigrationLock = 7_002_000_001;
    private const string ResourcePrefix = "Migrations.";

    public async Task<int> ApplyAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_lock(@key)", connection))
        {
            await lockCommand.With("key", MigrationLock).ExecuteNonQueryAsync(cancellationToken);
        }

        try
        {
            var applied = 0;
            foreach (var (version, sql) in LoadMigrations())
            {
                if (await IsAppliedAsync(connection, version, cancellationToken))
                {
                    continue;
                }

                LogApplying(logger, version);
                await using (var command = new NpgsqlCommand(sql, connection))
                {
                    await command.ExecuteNonQueryAsync(cancellationToken);
                }

                if (!await IsAppliedAsync(connection, version, cancellationToken))
                {
                    throw new InvalidOperationException($"Migration {version} ran but did not record itself in ops.schema_migration.");
                }

                applied++;
            }

            return applied;
        }
        finally
        {
            await using var unlock = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", connection);
            await unlock.With("key", MigrationLock).ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    internal static IReadOnlyList<(string Version, string Sql)> LoadMigrations()
    {
        var assembly = typeof(PostgresMigrator).Assembly;
        return assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal) && name.EndsWith(".sql", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(name =>
            {
                using var stream = assembly.GetManifestResourceStream(name)
                    ?? throw new InvalidOperationException($"Embedded migration {name} is missing.");
                using var reader = new StreamReader(stream);
                return (name[ResourcePrefix.Length..^".sql".Length], reader.ReadToEnd());
            })
            .ToList()
            .AsReadOnly();
    }

    private static async Task<bool> IsAppliedAsync(NpgsqlConnection connection, string version, CancellationToken cancellationToken)
    {
        await using (var exists = new NpgsqlCommand("SELECT to_regclass('ops.schema_migration') IS NOT NULL", connection))
        {
            if (await exists.ExecuteScalarAsync(cancellationToken) is not true)
            {
                return false;
            }
        }

        await using var applied = new NpgsqlCommand("SELECT count(*) FROM ops.schema_migration WHERE version = @version", connection);
        return await applied.With("version", version).ExecuteScalarAsync(cancellationToken) is 1L;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Applying database migration {Version}.")]
    private static partial void LogApplying(ILogger logger, string version);
}

/// <summary>
/// Startup preparation: applies migrations (when enabled) and makes sure every catalogue asset and
/// configured API client exists, because quotes, orders and ledger lines reference them.
/// </summary>
public sealed class PostgresStorageInitializer(NpgsqlDataSource dataSource, PostgresMigrator migrator, bool applyMigrations)
    : IStorageInitializer
{
    public async Task InitializeAsync(IEnumerable<Asset> assets, IEnumerable<ApiClientRecord> clients, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(clients);
        if (applyMigrations)
        {
            await migrator.ApplyAsync(cancellationToken);
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // Network coins first, so tokens can reference the coin their fees are paid in.
        var ordered = assets.OrderBy(asset => asset.NetworkFeeAsset is { } fee && fee != asset.Id ? 1 : 0).ToList();
        foreach (var asset in ordered)
        {
            await using var upsert = new NpgsqlCommand(
                """
                INSERT INTO gateway.asset (asset_key, kind, code, network, scale, network_fee_asset_key, name)
                VALUES (@key, @kind, @code, @network, @scale, @fee_asset, @name)
                ON CONFLICT (asset_key) DO UPDATE SET name = EXCLUDED.name, enabled = TRUE
                RETURNING scale
                """,
                connection,
                transaction);
            upsert
                .With("key", asset.Id.ToString())
                .With("kind", DbNames.Of(asset.Kind))
                .With("code", asset.Id.Code)
                .With("network", asset.Id.Network)
                .With("scale", (short)asset.Scale)
                .With("fee_asset", asset.NetworkFeeAsset?.ToString())
                .With("name", asset.Name);
            if (await upsert.ExecuteScalarAsync(cancellationToken) is short stored && stored != asset.Scale)
            {
                throw new InvalidOperationException(
                    $"{asset.Id} has {stored} decimal places in the database but {asset.Scale} in the catalogue; resolve this before starting.");
            }
        }

        foreach (var client in clients)
        {
            await using var upsert = new NpgsqlCommand(
                """
                INSERT INTO gateway.api_client (client_id, display_name, key_sha256, role)
                VALUES (@client, @client, @hash, @role)
                ON CONFLICT (client_id) DO UPDATE SET key_sha256 = EXCLUDED.key_sha256, role = EXCLUDED.role, revoked_at = NULL
                """,
                connection,
                transaction);
            await upsert
                .With("client", client.ClientId)
                .With("hash", Convert.ToHexStringLower(Convert.FromHexString(client.KeySha256)))
                .With("role", DbNames.Of(client.Role))
                .ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }
}

public static class PostgresServiceCollectionExtensions
{
    /// <summary>
    /// Durable storage in PostgreSQL, safe for several API instances. The connection string comes from
    /// configuration (for example the ConnectionStrings__Gateway environment variable), never from code.
    /// </summary>
    public static IServiceCollection AddPostgresStores(this IServiceCollection services, string connectionString, bool applyMigrationsOnStartup)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        services.AddSingleton<PostgresMigrator>();
        services.AddSingleton<IQuoteStore, PostgresQuoteStore>();
        services.AddSingleton<IOrderStore, PostgresOrderStore>();
        services.AddSingleton<ILedgerStore, PostgresLedgerStore>();
        services.AddSingleton<IIdempotencyStore, PostgresIdempotencyStore>();
        services.AddSingleton<IWebhookInbox, PostgresWebhookInbox>();
        services.AddSingleton<IOrderLocks, PostgresOrderLocks>();
        services.AddSingleton<IOutbox, PostgresOutbox>();
        services.AddSingleton<IReconciliationStore, PostgresReconciliationStore>();
        services.AddSingleton<ISettlementStore, PostgresSettlementStore>();
        services.AddSingleton<Application.Reviewers.IReviewerAccessStore, PostgresReviewerAccessStore>();
        services.AddSingleton<IStorageInitializer>(provider => new PostgresStorageInitializer(
            provider.GetRequiredService<NpgsqlDataSource>(),
            provider.GetRequiredService<PostgresMigrator>(),
            applyMigrationsOnStartup));
        return services;
    }
}
