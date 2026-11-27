using System.Data;
using FiatCryptoGateway.Application.Ports;
using FiatCryptoGateway.Application.Settlement;
using FiatCryptoGateway.Domain.Compliance;
using FiatCryptoGateway.Domain.Settlement;
using Npgsql;

namespace FiatCryptoGateway.Persistence.Postgres;

/// <summary>
/// Saves a merchant's settlement settings in one transaction: the version row is locked and checked, new wallets
/// are inserted, changed ones updated (the database allows only status and retirement to change), and new history
/// rows appended.
/// </summary>
public sealed class PostgresSettlementStore(NpgsqlDataSource dataSource) : ISettlementStore
{
    private const string WalletColumns =
        "w.wallet_id, w.client_id, w.network, w.address, w.label, w.status, w.screening_risk, w.screener, w.screening_reference, " +
        "w.registered_at, w.usable_from, w.retired_at";

    public async Task<MerchantSettlement> GetAsync(string clientId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        // One snapshot for the whole read. A merchant's settings, wallets and their histories are saved together in one transaction; read statement by statement,
        // a read that overlaps a save by another instance or a worker would see a wallet's row from before the save and its history from after it.
        await using var snapshot = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        int version;
        await using (var command = new NpgsqlCommand("SELECT row_version FROM gateway.merchant_settlement WHERE client_id = @client", connection))
        {
            command.With("client", clientId);
            var stored = await command.ExecuteScalarAsync(cancellationToken);
            if (stored is null)
            {
                return MerchantSettlement.Empty(clientId);
            }

            version = (int)stored;
        }

        var wallets = await ReadWalletsAsync(connection, "w.client_id = @client", command => command.With("client", clientId), cancellationToken);
        return MerchantSettlement.Restore(clientId, wallets, version);
    }

    public async Task SaveAsync(MerchantSettlement settlement, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settlement);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var ensure = new NpgsqlCommand(
            "INSERT INTO gateway.merchant_settlement (client_id) VALUES (@client) ON CONFLICT (client_id) DO NOTHING", connection, transaction))
        {
            ensure.With("client", settlement.ClientId);
            await ensure.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var check = new NpgsqlCommand(
            "SELECT row_version FROM gateway.merchant_settlement WHERE client_id = @client FOR UPDATE", connection, transaction))
        {
            check.With("client", settlement.ClientId);
            if ((int)(await check.ExecuteScalarAsync(cancellationToken))! != settlement.Version)
            {
                throw new SettlementConcurrencyException();
            }
        }

        var storedActions = new Dictionary<Guid, int>();
        await using (var existing = new NpgsqlCommand(
            """
            SELECT w.wallet_id, coalesce(max(a.sequence), 0)
              FROM gateway.settlement_wallet w
              LEFT JOIN gateway.settlement_wallet_action a USING (wallet_id)
             WHERE w.client_id = @client
             GROUP BY w.wallet_id
            """,
            connection,
            transaction))
        {
            existing.With("client", settlement.ClientId);
            await using var reader = await existing.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                storedActions[reader.GetGuid(0)] = reader.GetInt32(1);
            }
        }

        foreach (var wallet in settlement.Wallets)
        {
            if (storedActions.ContainsKey(wallet.WalletId))
            {
                await using var update = new NpgsqlCommand(
                    "UPDATE gateway.settlement_wallet SET status = @status, retired_at = @retired_at WHERE wallet_id = @id",
                    connection,
                    transaction);
                update.With("status", DbNames.Of(wallet.Status)).With("retired_at", wallet.RetiredAt?.Utc()).With("id", wallet.WalletId);
                await update.ExecuteNonQueryAsync(cancellationToken);
            }
            else
            {
                await using var insert = new NpgsqlCommand(
                    """
                    INSERT INTO gateway.settlement_wallet (
                        wallet_id, client_id, network, address, label, status, screening_risk, screener, screening_reference,
                        registered_at, usable_from, retired_at)
                    VALUES (@id, @client, @network, @address, @label, @status, @risk, @screener, @reference,
                            @registered_at, @usable_from, @retired_at)
                    """,
                    connection,
                    transaction);
                insert
                    .With("id", wallet.WalletId)
                    .With("client", wallet.ClientId)
                    .With("network", wallet.Network)
                    .With("address", wallet.Address)
                    .With("label", wallet.Label)
                    .With("status", DbNames.Of(wallet.Status))
                    .With("risk", DbNames.Of(wallet.Screening.Risk))
                    .With("screener", wallet.Screening.Screener)
                    .With("reference", wallet.Screening.Reference)
                    .With("registered_at", wallet.RegisteredAt.Utc())
                    .With("usable_from", wallet.UsableFrom.Utc())
                    .With("retired_at", wallet.RetiredAt?.Utc());
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            var stored = storedActions.GetValueOrDefault(wallet.WalletId);
            foreach (var action in wallet.History.Where(action => action.Sequence > stored))
            {
                await using var append = new NpgsqlCommand(
                    """
                    INSERT INTO gateway.settlement_wallet_action (wallet_id, sequence, action, actor, reason, occurred_at)
                    VALUES (@id, @sequence, @action, @actor, @reason, @occurred_at)
                    """,
                    connection,
                    transaction);
                append
                    .With("id", wallet.WalletId)
                    .With("sequence", action.Sequence)
                    .With("action", DbNames.Of(action.Kind))
                    .With("actor", action.Actor)
                    .With("reason", action.Reason)
                    .With("occurred_at", action.OccurredAt.Utc());
                await append.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await OutboxRows.InsertAsync(connection, transaction, SettlementNotifications.ForNewActions(settlement, storedActions), cancellationToken);

        await using (var bump = new NpgsqlCommand(
            "UPDATE gateway.merchant_settlement SET row_version = row_version + 1 WHERE client_id = @client", connection, transaction))
        {
            bump.With("client", settlement.ClientId);
            await bump.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        settlement.Version++;
    }

    public async Task<string?> FindOwnerAsync(Guid walletId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT client_id FROM gateway.settlement_wallet WHERE wallet_id = @id", connection);
        command.With("id", walletId);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    public async Task<IReadOnlyList<SettlementWallet>> ListUnderReviewAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        // One snapshot for the whole read. A wallet and its history are saved together in one transaction; read statement by statement,
        // a read that overlaps a save by another instance or a worker would see the wallet's row from before the save and its history from after it.
        await using var snapshot = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        return await ReadWalletsAsync(connection, "w.status = 'UNDER_REVIEW'", _ => { }, cancellationToken);
    }

    private static async Task<IReadOnlyList<SettlementWallet>> ReadWalletsAsync(
        NpgsqlConnection connection,
        string filter,
        Action<NpgsqlCommand> bind,
        CancellationToken cancellationToken)
    {
        var rows = new List<(Guid Id, string Client, string Network, string Address, string? Label, SettlementWalletStatus Status,
            WalletScreeningResult Screening, DateTimeOffset RegisteredAt, DateTimeOffset UsableFrom, DateTimeOffset? RetiredAt)>();
        await using (var command = new NpgsqlCommand(
            $"SELECT {WalletColumns} FROM gateway.settlement_wallet w WHERE {filter} ORDER BY w.registered_at, w.wallet_number", connection))
        {
            bind(command);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    DbNames.Parse<SettlementWalletStatus>(reader.GetString(5)),
                    new WalletScreeningResult(DbNames.Parse<WalletRisk>(reader.GetString(6)), reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8)),
                    reader.GetFieldValue<DateTimeOffset>(9),
                    reader.GetFieldValue<DateTimeOffset>(10),
                    reader.IsDBNull(11) ? null : reader.GetFieldValue<DateTimeOffset>(11)));
            }
        }

        var history = rows.ToDictionary(row => row.Id, _ => new List<SettlementWalletAction>());
        if (rows.Count > 0)
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT wallet_id, sequence, action, actor, reason, occurred_at
                  FROM gateway.settlement_wallet_action
                 WHERE wallet_id = ANY(@ids)
                 ORDER BY wallet_id, sequence
                """,
                connection);
            command.With("ids", rows.Select(row => row.Id).ToArray());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                history[reader.GetGuid(0)].Add(new SettlementWalletAction(
                    reader.GetInt32(1),
                    DbNames.Parse<SettlementWalletActionKind>(reader.GetString(2)),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetFieldValue<DateTimeOffset>(5)));
            }
        }

        return rows
            .Select(row => SettlementWallet.Restore(
                row.Id, row.Client, row.Network, row.Address, row.Label, row.Screening, row.RegisteredAt, row.UsableFrom, row.Status, row.RetiredAt, history[row.Id]))
            .ToList()
            .AsReadOnly();
    }
}
