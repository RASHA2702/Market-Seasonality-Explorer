using FiatCryptoGateway.Domain.Assets;
using FiatCryptoGateway.Domain.Orders;

namespace FiatCryptoGateway.Domain.Ledger;

public enum EntrySide
{
    Debit,
    Credit,
}

/// <summary>
/// An account in one asset. Asset accounts normally carry debit balances; liability, revenue and equity
/// accounts normally carry credit balances. Journal lines carry the order ID, so one customer-orders
/// account per asset is enough to reconcile each order.
/// </summary>
public sealed record LedgerAccount(string Code, AssetId Asset)
{
    public static LedgerAccount PspClearing(string provider, AssetId asset) => new($"assets:psp_clearing:{provider}", asset);

    public static LedgerAccount HotWallet(string custodian, AssetId asset) => new($"assets:hot_wallet:{custodian}", asset);

    public static LedgerAccount CustomerOrders(AssetId asset) => new("liabilities:customer_orders", asset);

    public static LedgerAccount LiquidityPayable(string provider, AssetId asset) => new($"liabilities:liquidity_provider:{provider}", asset);

    public static LedgerAccount ServiceFees(AssetId asset) => new("revenue:service_fees", asset);

    public static LedgerAccount Spread(AssetId asset) => new("revenue:spread", asset);

    public static LedgerAccount NetworkFeeRecovery(AssetId asset) => new("revenue:network_fee_recovery", asset);

    public static LedgerAccount NetworkFees(AssetId asset) => new("expenses:network_fees", asset);

    public static LedgerAccount OwnerFunding(AssetId asset) => new("equity:owner_funding", asset);
}

public sealed record JournalLine(LedgerAccount Account, EntrySide Side, Money Amount);

/// <summary>Kinds of journal entry. Provider settlement reports use the same names for the movements they list.</summary>
public static class EntryKinds
{
    public const string FiatReceived = "FIAT_RECEIVED";
    public const string CryptoPurchased = "CRYPTO_PURCHASED";
    public const string CryptoSent = "CRYPTO_SENT";
    public const string FiatRefunded = "FIAT_REFUNDED";
}

/// <summary>
/// The provider's own record of the money movement an entry accounts for: the provider, its ID for the
/// movement, the amount it moved and when (by the provider's clock). Daily reconciliation matches these
/// against the provider's settlement report.
/// </summary>
public sealed record ExternalReference(string Provider, string Id, Money Amount, DateTimeOffset OccurredAt);

/// <summary>
/// An immutable, balanced set of lines. Debits equal credits separately for every asset, so a
/// cross-currency conversion can never balance by mixing units. Corrections are new entries, never edits.
/// </summary>
public sealed class JournalEntry
{
    public JournalEntry(
        Guid entryId,
        string kind,
        Guid? orderId,
        DateTimeOffset postedAt,
        IReadOnlyList<JournalLine> lines,
        ExternalReference? external = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(lines);

        if (external is not null &&
            (string.IsNullOrWhiteSpace(external.Provider) || string.IsNullOrWhiteSpace(external.Id) || !external.Amount.IsPositive))
        {
            throw new GatewayRuleException("INVALID_EXTERNAL_REFERENCE", "A provider reference needs a provider, an ID and a positive amount.");
        }

        if (entryId == Guid.Empty)
        {
            throw new ArgumentException("An entry ID is required.", nameof(entryId));
        }

        if (lines.Count < 2)
        {
            throw new GatewayRuleException("UNBALANCED_ENTRY", "A journal entry needs at least two lines.");
        }

        foreach (var line in lines)
        {
            if (!line.Amount.IsPositive)
            {
                throw new GatewayRuleException("INVALID_LINE", "Journal line amounts must be positive.");
            }

            if (line.Amount.Asset.Id != line.Account.Asset)
            {
                throw new GatewayRuleException("ACCOUNT_ASSET_MISMATCH", $"{line.Account.Code} holds {line.Account.Asset}, not {line.Amount.Asset.Id}.");
            }
        }

        foreach (var asset in lines.GroupBy(line => line.Amount.Asset.Id))
        {
            var net = asset.Sum(line => line.Side == EntrySide.Debit ? line.Amount.Amount : -line.Amount.Amount);
            if (net != 0)
            {
                throw new GatewayRuleException("UNBALANCED_ENTRY", $"Debits and credits differ by {net} {asset.Key}.");
            }
        }

        EntryId = entryId;
        Kind = kind;
        OrderId = orderId;
        PostedAt = postedAt;
        Lines = [.. lines];
        External = external;
    }

    public Guid EntryId { get; }

    public string Kind { get; }

    public Guid? OrderId { get; }

    public DateTimeOffset PostedAt { get; }

    public IReadOnlyList<JournalLine> Lines { get; }

    /// <summary>The provider movement this entry records, or null for an internal entry.</summary>
    public ExternalReference? External { get; }
}

/// <summary>Collects lines; skips zero amounts and turns a negative amount into the opposite side.</summary>
public sealed class JournalEntryBuilder
{
    private readonly List<JournalLine> _lines = [];

    public JournalEntryBuilder Debit(LedgerAccount account, Money amount) => Add(account, EntrySide.Debit, amount);

    public JournalEntryBuilder Credit(LedgerAccount account, Money amount) => Add(account, EntrySide.Credit, amount);

    public JournalEntry Build(string kind, Guid? orderId, DateTimeOffset postedAt, ExternalReference? external = null) =>
        new(Guid.NewGuid(), kind, orderId, postedAt, _lines, external);

    private JournalEntryBuilder Add(LedgerAccount account, EntrySide side, Money amount)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(amount);

        if (amount.Amount == 0)
        {
            return this;
        }

        if (amount.IsNegative)
        {
            side = side == EntrySide.Debit ? EntrySide.Credit : EntrySide.Debit;
            amount = amount.Negate();
        }

        _lines.Add(new JournalLine(account, side, amount));
        return this;
    }
}

public sealed record AccountBalance(string Account, AssetId Asset, decimal DebitMinusCredit);

public static class LedgerBalances
{
    /// <summary>Balances per account and asset, debit-positive. For a correct ledger they sum to zero per asset.</summary>
    public static IReadOnlyList<AccountBalance> Compute(IEnumerable<JournalEntry> entries, Guid? orderId = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return entries
            .Where(entry => orderId is null || entry.OrderId == orderId)
            .SelectMany(entry => entry.Lines)
            .GroupBy(line => (line.Account.Code, line.Account.Asset))
            .Select(group => new AccountBalance(
                group.Key.Code,
                group.Key.Asset,
                group.Sum(line => line.Side == EntrySide.Debit ? line.Amount.Amount : -line.Amount.Amount)))
            .OrderBy(balance => balance.Account, StringComparer.Ordinal)
            .ThenBy(balance => balance.Asset.ToString(), StringComparer.Ordinal)
            .ToList();
    }
}

/// <summary>
/// The journal entries an on-ramp order produces. After completion the customer-orders liability for the
/// order is zero in every asset: the customer's fiat paid the fee and the purchase, and the crypto bought
/// for them was delivered plus the network fee they were charged.
/// </summary>
public static class OnRampPostings
{
    public static JournalEntry FiatReceived(PaymentOrder order, FiatPaymentConfirmation payment, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(payment);
        var fiat = payment.Amount.Asset.Id;
        return new JournalEntryBuilder()
            .Debit(LedgerAccount.PspClearing(payment.Provider, fiat), payment.Amount)
            .Credit(LedgerAccount.CustomerOrders(fiat), payment.Amount)
            .Build(
                EntryKinds.FiatReceived,
                order.OrderId,
                at,
                new ExternalReference(payment.Provider, payment.ProviderPaymentId, payment.Amount, payment.CapturedAt));
    }

    public static JournalEntry CryptoPurchased(PaymentOrder order, LiquidityExecution execution, string custodian, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(execution);
        var quote = order.Quote;
        var fiat = quote.SourceAmount.Asset.Id;
        var crypto = quote.DestinationAmount.Asset.Id;

        // Positive when the provider delivered more than the customer is owed (spread earned);
        // negative when execution was worse than the quote (slippage absorbed by the gateway).
        var spread = execution.CryptoReceived.Subtract(quote.GrossDestinationAmount);

        return new JournalEntryBuilder()
            .Debit(LedgerAccount.CustomerOrders(fiat), quote.ServiceFee)
            .Credit(LedgerAccount.ServiceFees(fiat), quote.ServiceFee)
            .Debit(LedgerAccount.CustomerOrders(fiat), quote.NetSourceAmount)
            .Credit(LedgerAccount.LiquidityPayable(execution.Provider, fiat), execution.FiatSpent)
            .Debit(LedgerAccount.HotWallet(custodian, crypto), execution.CryptoReceived)
            .Credit(LedgerAccount.CustomerOrders(crypto), quote.GrossDestinationAmount)
            .Credit(LedgerAccount.Spread(crypto), spread)
            .Build(
                EntryKinds.CryptoPurchased,
                order.OrderId,
                at,
                new ExternalReference(execution.Provider, execution.ExecutionId, execution.CryptoReceived, execution.ExecutedAt));
    }

    public static JournalEntry CryptoSent(PaymentOrder order, CryptoTransfer transfer, string custodian, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(transfer);
        var quote = order.Quote;
        var crypto = quote.DestinationAmount.Asset.Id;
        var feeAsset = transfer.NetworkFeePaid.Asset.Id;

        return new JournalEntryBuilder()
            .Debit(LedgerAccount.CustomerOrders(crypto), quote.GrossDestinationAmount)
            .Credit(LedgerAccount.HotWallet(custodian, crypto), transfer.Amount)
            .Credit(LedgerAccount.NetworkFeeRecovery(crypto), quote.NetworkFee)
            .Debit(LedgerAccount.NetworkFees(feeAsset), transfer.NetworkFeePaid)
            .Credit(LedgerAccount.HotWallet(custodian, feeAsset), transfer.NetworkFeePaid)
            .Build(
                EntryKinds.CryptoSent,
                order.OrderId,
                at,
                new ExternalReference(transfer.Provider, transfer.TransactionHash, transfer.Amount, transfer.BroadcastAt));
    }

    public static JournalEntry Refunded(PaymentOrder order, FiatRefund refund, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(refund);
        var fiat = refund.Amount.Asset.Id;
        return new JournalEntryBuilder()
            .Debit(LedgerAccount.CustomerOrders(fiat), refund.Amount)
            .Credit(LedgerAccount.PspClearing(refund.Provider, fiat), refund.Amount)
            .Build(
                EntryKinds.FiatRefunded,
                order.OrderId,
                at,
                new ExternalReference(refund.Provider, refund.RefundId, refund.Amount, refund.RefundedAt));
    }
}
