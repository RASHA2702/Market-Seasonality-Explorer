using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using FiatCryptoGateway.Domain.Assets;
using FiatCryptoGateway.Domain.Compliance;
using FiatCryptoGateway.Domain.Ledger;
using FiatCryptoGateway.Domain.Orders;
using FiatCryptoGateway.Domain.Pricing;
using Npgsql;
using NpgsqlTypes;

namespace FiatCryptoGateway.Persistence.Postgres;

/// <summary>C# enum names stored as UPPER_SNAKE_CASE, matching the database's check lists.</summary>
internal static partial class DbNames
{
    public static string Of<TEnum>(TEnum value)
        where TEnum : struct, Enum =>
        PascalBoundary().Replace(value.ToString(), "_$1").ToUpperInvariant();

    public static TEnum Parse<TEnum>(string name)
        where TEnum : struct, Enum
    {
        foreach (var value in Enum.GetValues<TEnum>())
        {
            if (string.Equals(Of(value), name, StringComparison.Ordinal))
            {
                return value;
            }
        }

        throw new InvalidOperationException($"'{name}' is not a known {typeof(TEnum).Name} value.");
    }

    [GeneratedRegex("(?<!^)([A-Z])", RegexOptions.CultureInvariant)]
    private static partial Regex PascalBoundary();
}

internal static class CommandExtensions
{
    public static NpgsqlCommand With(this NpgsqlCommand command, string name, object? value)
    {
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    public static NpgsqlCommand WithJson(this NpgsqlCommand command, string name, string? json)
    {
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Jsonb) { Value = (object?)json ?? DBNull.Value });
        return command;
    }

    /// <summary>PostgreSQL stores instants in UTC; Npgsql refuses non-UTC offsets.</summary>
    public static DateTimeOffset Utc(this DateTimeOffset value) => value.ToUniversalTime();
}

internal sealed record MoneyRecord(string Asset, string Amount);

internal sealed record PriceRecord(string Base, string Quote, string Rate, DateTimeOffset ObservedAt, DateTimeOffset AvailableAt, string Source);

internal sealed record FiatPaymentRecord(string Provider, string ProviderPaymentId, MoneyRecord Amount, DateTimeOffset CapturedAt);

internal sealed record PurchaseRecord(string Provider, string ExecutionId, MoneyRecord FiatSpent, MoneyRecord CryptoReceived, DateTimeOffset ExecutedAt);

internal sealed record TransferRecord(
    string Provider,
    string TransactionHash,
    MoneyRecord Amount,
    string DestinationAddress,
    MoneyRecord NetworkFeePaid,
    DateTimeOffset BroadcastAt);

internal sealed record RefundRecord(string Provider, string RefundId, MoneyRecord Amount, DateTimeOffset RefundedAt);

/// <summary>Converts between domain objects and their stored form. Amounts are stored as exact strings.</summary>
internal sealed class RecordMapper(AssetCatalog catalog)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Asset Asset(string key) => catalog.Get(AssetId.Parse(key));

    public Money Money(decimal amount, string assetKey) => new(amount, Asset(assetKey));

    public static string? Serialize(FiatPaymentConfirmation? payment) => payment is null
        ? null
        : JsonSerializer.Serialize(new FiatPaymentRecord(payment.Provider, payment.ProviderPaymentId, ToRecord(payment.Amount), payment.CapturedAt.Utc()), Json);

    public static string? Serialize(LiquidityExecution? purchase) => purchase is null
        ? null
        : JsonSerializer.Serialize(
            new PurchaseRecord(purchase.Provider, purchase.ExecutionId, ToRecord(purchase.FiatSpent), ToRecord(purchase.CryptoReceived), purchase.ExecutedAt.Utc()),
            Json);

    public static string? Serialize(CryptoTransfer? transfer) => transfer is null
        ? null
        : JsonSerializer.Serialize(
            new TransferRecord(
                transfer.Provider,
                transfer.TransactionHash,
                ToRecord(transfer.Amount),
                transfer.DestinationAddress,
                ToRecord(transfer.NetworkFeePaid),
                transfer.BroadcastAt.Utc()),
            Json);

    public static string? Serialize(FiatRefund? refund) => refund is null
        ? null
        : JsonSerializer.Serialize(new RefundRecord(refund.Provider, refund.RefundId, ToRecord(refund.Amount), refund.RefundedAt.Utc()), Json);

    public static string Serialize(IEnumerable<PriceObservation> path) =>
        JsonSerializer.Serialize(
            path.Select(price => new PriceRecord(
                price.BaseAsset.ToString(),
                price.QuoteAsset.ToString(),
                price.Rate.ToString(CultureInfo.InvariantCulture),
                price.ObservedAt.Utc(),
                price.AvailableAt.Utc(),
                price.Source)),
            Json);

    public static string Serialize(IReadOnlyList<string> reasons) => JsonSerializer.Serialize(reasons, Json);

    public FiatPaymentConfirmation? FiatPayment(string? json, Guid orderId) =>
        Read<FiatPaymentRecord>(json) is { } record
            ? new FiatPaymentConfirmation(record.Provider, record.ProviderPaymentId, orderId, Money(record.Amount), record.CapturedAt)
            : null;

    public LiquidityExecution? Purchase(string? json, Guid orderId) =>
        Read<PurchaseRecord>(json) is { } record
            ? new LiquidityExecution(record.Provider, record.ExecutionId, orderId, Money(record.FiatSpent), Money(record.CryptoReceived), record.ExecutedAt)
            : null;

    public CryptoTransfer? Transfer(string? json, Guid orderId) =>
        Read<TransferRecord>(json) is { } record
            ? new CryptoTransfer(
                record.Provider,
                record.TransactionHash,
                orderId,
                Money(record.Amount),
                record.DestinationAddress,
                Money(record.NetworkFeePaid),
                record.BroadcastAt)
            : null;

    public FiatRefund? Refund(string? json, Guid orderId) =>
        Read<RefundRecord>(json) is { } record
            ? new FiatRefund(record.Provider, record.RefundId, orderId, Money(record.Amount), record.RefundedAt)
            : null;

    public static IReadOnlyList<PriceObservation> PricePath(string json) =>
        (JsonSerializer.Deserialize<List<PriceRecord>>(json, Json) ?? [])
            .Select(record => new PriceObservation(
                AssetId.Parse(record.Base),
                AssetId.Parse(record.Quote),
                decimal.Parse(record.Rate, NumberStyles.Number, CultureInfo.InvariantCulture),
                record.ObservedAt,
                record.AvailableAt,
                record.Source))
            .ToList()
            .AsReadOnly();

    public static IReadOnlyList<string> Reasons(string json) =>
        (JsonSerializer.Deserialize<List<string>>(json, Json) ?? []).AsReadOnly();

    private static MoneyRecord ToRecord(Money money) =>
        new(money.Asset.Id.ToString(), money.Amount.ToString(CultureInfo.InvariantCulture));

    private Money Money(MoneyRecord record) =>
        Money(decimal.Parse(record.Amount, NumberStyles.Number, CultureInfo.InvariantCulture), record.Asset);

    private static T? Read<T>(string? json)
        where T : class =>
        json is null ? null : JsonSerializer.Deserialize<T>(json, Json);
}

/// <summary>Reads and writes quotes; shared by the quote store and the order store.</summary>
internal static class QuoteRows
{
    public static async Task InsertAsync(NpgsqlConnection connection, Quote quote, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO gateway.quote (
                quote_id, client_id, source_asset_key, source_amount, service_fee, net_source_amount,
                destination_asset_key, gross_destination_amount, network_fee, destination_amount,
                market_rate, customer_rate, pivot_asset_key, pivot_amount, price_path, pricing_policy_version,
                created_at, expires_at)
            VALUES (
                @id, @client, @source_asset, @source_amount, @service_fee, @net_source_amount,
                @destination_asset, @gross_destination_amount, @network_fee, @destination_amount,
                @market_rate, @customer_rate, @pivot_asset, @pivot_amount, @price_path, @policy_version,
                @created_at, @expires_at)
            """,
            connection);
        command
            .With("id", quote.QuoteId)
            .With("client", quote.ClientId)
            .With("source_asset", quote.SourceAmount.Asset.Id.ToString())
            .With("source_amount", quote.SourceAmount.Amount)
            .With("service_fee", quote.ServiceFee.Amount)
            .With("net_source_amount", quote.NetSourceAmount.Amount)
            .With("destination_asset", quote.DestinationAmount.Asset.Id.ToString())
            .With("gross_destination_amount", quote.GrossDestinationAmount.Amount)
            .With("network_fee", quote.NetworkFee.Amount)
            .With("destination_amount", quote.DestinationAmount.Amount)
            .With("market_rate", quote.MarketRate)
            .With("customer_rate", quote.CustomerRate)
            .With("pivot_asset", quote.PivotAmount.Asset.Id.ToString())
            .With("pivot_amount", quote.PivotAmount.Amount)
            .WithJson("price_path", RecordMapper.Serialize(quote.PricePath))
            .With("policy_version", quote.PricingPolicyVersion)
            .With("created_at", quote.CreatedAt.Utc())
            .With("expires_at", quote.ExpiresAt.Utc());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task<Quote?> ReadAsync(
        NpgsqlConnection connection,
        Guid quoteId,
        RecordMapper mapper,
        CancellationToken cancellationToken)
    {
        // trim_scale drops the padding zeros of NUMERIC(38,18) so every value fits .NET's decimal exactly.
        await using var command = new NpgsqlCommand(
            """
            SELECT client_id, source_asset_key, trim_scale(source_amount), trim_scale(service_fee),
                   trim_scale(net_source_amount), destination_asset_key, trim_scale(gross_destination_amount),
                   trim_scale(network_fee), trim_scale(destination_amount), market_rate, customer_rate,
                   pivot_asset_key, trim_scale(pivot_amount), price_path::text, pricing_policy_version,
                   created_at, expires_at
              FROM gateway.quote
             WHERE quote_id = @id
            """,
            connection);
        command.With("id", quoteId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var sourceAsset = reader.GetString(1);
        var destinationAsset = reader.GetString(5);
        return new Quote(
            quoteId,
            reader.GetString(0),
            mapper.Money(reader.GetDecimal(2), sourceAsset),
            mapper.Money(reader.GetDecimal(3), sourceAsset),
            mapper.Money(reader.GetDecimal(4), sourceAsset),
            reader.GetDecimal(9),
            reader.GetDecimal(10),
            mapper.Money(reader.GetDecimal(6), destinationAsset),
            mapper.Money(reader.GetDecimal(7), destinationAsset),
            mapper.Money(reader.GetDecimal(8), destinationAsset),
            mapper.Money(reader.GetDecimal(12), reader.GetString(11)),
            RecordMapper.PricePath(reader.GetString(13)),
            reader.GetString(14),
            reader.GetFieldValue<DateTimeOffset>(15),
            reader.GetFieldValue<DateTimeOffset>(16));
    }
}

/// <summary>Writes journal entries inside the caller's transaction; the database checks the balance at commit.</summary>
internal static class LedgerRows
{
    public static async Task InsertAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IEnumerable<JournalEntry> entries,
        CancellationToken cancellationToken)
    {
        foreach (var entry in entries)
        {
            await using (var command = new NpgsqlCommand(
                """
                INSERT INTO ledger.journal_entry (
                    entry_id, kind, order_id, posted_at,
                    external_provider, external_id, external_asset_key, external_amount, external_occurred_at)
                VALUES (@id, @kind, @order_id, @posted_at, @provider, @external_id, @external_asset, @external_amount, @external_at)
                """,
                connection,
                transaction))
            {
                var external = entry.External;
                command
                    .With("id", entry.EntryId)
                    .With("kind", entry.Kind)
                    .With("order_id", entry.OrderId)
                    .With("posted_at", entry.PostedAt.Utc())
                    .With("provider", external?.Provider)
                    .With("external_id", external?.Id)
                    .With("external_asset", external?.Amount.Asset.Id.ToString())
                    .With("external_amount", external?.Amount.Amount)
                    .With("external_at", external?.OccurredAt.Utc());
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            for (var index = 0; index < entry.Lines.Count; index++)
            {
                var line = entry.Lines[index];
                await using var command = new NpgsqlCommand(
                    """
                    INSERT INTO ledger.journal_line (entry_id, line_number, account_code, asset_key, side, amount)
                    VALUES (@entry_id, @line_number, @account, @asset, @side, @amount)
                    """,
                    connection,
                    transaction);
                command
                    .With("entry_id", entry.EntryId)
                    .With("line_number", (short)(index + 1))
                    .With("account", line.Account.Code)
                    .With("asset", line.Account.Asset.ToString())
                    .With("side", DbNames.Of(line.Side))
                    .With("amount", line.Amount.Amount);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }
    }
}

internal static class ComplianceRows
{
    public static ComplianceDecision Read(NpgsqlDataReader reader, Guid orderId) =>
        new(
            orderId,
            DbNames.Parse<ComplianceOutcome>(reader.GetString(0)),
            RecordMapper.Reasons(reader.GetString(1)),
            reader.GetString(2),
            reader.GetFieldValue<DateTimeOffset>(3));
}
