using System.Text.Json;
using FiatCryptoGateway.Application.Ports;
using FiatCryptoGateway.Domain.Compliance;
using FiatCryptoGateway.Domain.Settlement;

namespace FiatCryptoGateway.Application.Settlement;

/// <summary>
/// Tells a merchant about every change to its settlement wallets: a registration (so a wallet added without its
/// knowledge, for example with a stolen key, can be disabled during its cooling-off period), a decision and a
/// withdrawal. Notifications use the merchant's view: never the screening, the reviewer or their reasons.
/// </summary>
public static class SettlementNotifications
{
    public const string WalletAggregate = "settlement_wallet";
    public const string StatusChanged = "settlement_wallet.status_changed";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>One message per wallet history entry beyond the counts already stored in <paramref name="storedActions"/>.</summary>
    public static IReadOnlyList<OutboxMessage> ForNewActions(MerchantSettlement settlement, IReadOnlyDictionary<Guid, int> storedActions)
    {
        ArgumentNullException.ThrowIfNull(settlement);
        ArgumentNullException.ThrowIfNull(storedActions);
        var messages = new List<OutboxMessage>();
        foreach (var wallet in settlement.Wallets)
        {
            var stored = storedActions.GetValueOrDefault(wallet.WalletId);
            foreach (var action in wallet.History.Where(action => action.Sequence > stored))
            {
                var messageId = Guid.NewGuid();
                var payload = JsonSerializer.Serialize(
                    new WalletNotification(
                        messageId,
                        StatusChanged,
                        action.OccurredAt,
                        new WalletData(
                            wallet.WalletId,
                            wallet.Network,
                            wallet.Address,
                            wallet.Label,
                            StatusAfter(action, wallet),
                            wallet.UsableFrom,
                            action.Sequence,
                            action.OccurredAt)),
                    Json);
                messages.Add(new OutboxMessage(
                    messageId, settlement.ClientId, WalletAggregate, wallet.WalletId, action.Sequence, StatusChanged, payload, 0, action.OccurredAt));
            }
        }

        return messages.AsReadOnly();
    }

    /// <summary>The status a merchant saw right after <paramref name="action"/>, worked out from the history itself.</summary>
    private static string StatusAfter(SettlementWalletAction action, SettlementWallet wallet) => action.Kind switch
    {
        SettlementWalletActionKind.Registered when wallet.Screening.Risk != WalletRisk.Low => "UnderReview",
        SettlementWalletActionKind.Registered or SettlementWalletActionKind.Approved =>
            action.OccurredAt < wallet.UsableFrom ? "CoolingOff" : "Active",
        SettlementWalletActionKind.Rejected => "NotAccepted",
        _ => "Disabled",
    };

    private sealed record WalletNotification(Guid Id, string Type, DateTimeOffset CreatedAt, WalletData Data);

    private sealed record WalletData(
        Guid WalletId,
        string Network,
        string Address,
        string? Label,
        string Status,
        DateTimeOffset UsableFrom,
        int Sequence,
        DateTimeOffset OccurredAt);
}
