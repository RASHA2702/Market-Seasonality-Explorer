using FiatCryptoGateway.Domain.Compliance;

namespace FiatCryptoGateway.Domain.Settlement;

public enum SettlementWalletStatus
{
    /// <summary>Screened clean, or approved by a reviewer. Usable from <see cref="SettlementWallet.UsableFrom"/> until replaced.</summary>
    Approved,

    /// <summary>Screening found risk or could not finish; a reviewer decides.</summary>
    UnderReview,

    /// <summary>A reviewer declined it. Final.</summary>
    Rejected,

    /// <summary>Withdrawn by the merchant or a reviewer. Final.</summary>
    Disabled,
}

public enum SettlementWalletActionKind
{
    Registered,
    Approved,
    Rejected,
    Disabled,
}

/// <summary>One entry in a wallet's history: who did what, when and why.</summary>
public sealed record SettlementWalletAction(int Sequence, SettlementWalletActionKind Kind, string Actor, string? Reason, DateTimeOffset OccurredAt);

/// <summary>A wallet a merchant is paid into on one network. Its address and screening never change.</summary>
public sealed class SettlementWallet
{
    private readonly List<SettlementWalletAction> _history = [];

    internal SettlementWallet(
        Guid walletId,
        string clientId,
        string network,
        string address,
        string? label,
        WalletScreeningResult screening,
        DateTimeOffset registeredAt,
        DateTimeOffset usableFrom,
        SettlementWalletStatus status,
        DateTimeOffset? retiredAt = null)
    {
        WalletId = walletId;
        ClientId = clientId;
        Network = network;
        Address = address;
        Label = label;
        Screening = screening;
        RegisteredAt = registeredAt;
        UsableFrom = usableFrom;
        Status = status;
        RetiredAt = retiredAt;
    }

    public Guid WalletId { get; }

    public string ClientId { get; }

    public string Network { get; }

    public string Address { get; }

    public string? Label { get; }

    /// <summary>The screening at registration. Merchants never see it.</summary>
    public WalletScreeningResult Screening { get; }

    public DateTimeOffset RegisteredAt { get; }

    /// <summary>When the cooling-off period ends and the wallet can first receive payments (once approved).</summary>
    public DateTimeOffset UsableFrom { get; }

    /// <summary>When a newer wallet for the same network takes over, if one has been scheduled to.</summary>
    public DateTimeOffset? RetiredAt { get; internal set; }

    public SettlementWalletStatus Status { get; internal set; }

    public IReadOnlyList<SettlementWalletAction> History => _history.AsReadOnly();

    public bool IsUsableAt(DateTimeOffset now) =>
        Status == SettlementWalletStatus.Approved && now >= UsableFrom && (RetiredAt is null || now < RetiredAt);

    /// <summary>Not usable yet, but may become so: under review, or approved and still cooling off.</summary>
    public bool IsPendingAt(DateTimeOffset now) =>
        Status == SettlementWalletStatus.UnderReview || (Status == SettlementWalletStatus.Approved && now < UsableFrom);

    internal static SettlementWallet Restore(
        Guid walletId,
        string clientId,
        string network,
        string address,
        string? label,
        WalletScreeningResult screening,
        DateTimeOffset registeredAt,
        DateTimeOffset usableFrom,
        SettlementWalletStatus status,
        DateTimeOffset? retiredAt,
        IEnumerable<SettlementWalletAction> history)
    {
        var wallet = new SettlementWallet(walletId, clientId, network, address, label, screening, registeredAt, usableFrom, status, retiredAt);
        wallet._history.AddRange(history.OrderBy(action => action.Sequence));
        return wallet;
    }

    internal SettlementWallet Copy() =>
        Restore(WalletId, ClientId, Network, Address, Label, Screening, RegisteredAt, UsableFrom, Status, RetiredAt, _history);

    internal void Record(SettlementWalletActionKind kind, string actor, string? reason, DateTimeOffset at) =>
        _history.Add(new SettlementWalletAction(_history.Count + 1, kind, actor, reason, at));
}

/// <summary>
/// Where one merchant is paid. Each network has at most one wallet in use at a time:
/// <list type="bullet">
/// <item>A new wallet is screened when registered. A clean one is approved at once but can receive payments only
/// after a cooling-off period, so a merchant can spot and disable a wallet it did not add (for example one added
/// with a stolen API key). A risky one, or one whose screening could not finish, waits for a reviewer; a blocked
/// one can never be approved.</item>
/// <item>A replacement takes over from the wallet in use the moment it becomes usable. Until then the old wallet
/// keeps receiving payments, and if the replacement is withdrawn first, the old wallet simply stays in use.</item>
/// <item>Only one change per network can be waiting at a time.</item>
/// </list>
/// </summary>
public sealed class MerchantSettlement
{
    public const int MaxLabelLength = 100;
    public const int MaxReasonLength = 2_000;

    private readonly List<SettlementWallet> _wallets;

    private MerchantSettlement(string clientId, IEnumerable<SettlementWallet> wallets, int version)
    {
        ClientId = clientId;
        _wallets = [.. wallets.OrderBy(wallet => wallet.RegisteredAt)];
        Version = version;
    }

    public string ClientId { get; }

    /// <summary>Oldest first.</summary>
    public IReadOnlyList<SettlementWallet> Wallets => _wallets.AsReadOnly();

    /// <summary>The stored version this copy was loaded at, used to refuse saving over a newer change.</summary>
    public int Version { get; internal set; }

    public static MerchantSettlement Empty(string clientId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        return new MerchantSettlement(clientId, [], version: 0);
    }

    /// <summary>The wallet that receives payments on <paramref name="network"/> now, if any.</summary>
    public SettlementWallet? Current(string network, DateTimeOffset now) =>
        _wallets.LastOrDefault(wallet => wallet.Network == network && wallet.IsUsableAt(now));

    public SettlementWallet? Find(Guid walletId) => _wallets.FirstOrDefault(wallet => wallet.WalletId == walletId);

    /// <summary>
    /// Registers a wallet with its screening result. Registering a wallet that is already in use or waiting returns
    /// it unchanged (<paramref name="created"/> is false).
    /// </summary>
    public SettlementWallet Register(
        Guid walletId,
        string network,
        string address,
        string? label,
        WalletScreeningResult screening,
        string actor,
        DateTimeOffset now,
        TimeSpan coolingOff,
        out bool created)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(network);
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        ArgumentNullException.ThrowIfNull(screening);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ArgumentOutOfRangeException.ThrowIfLessThan(coolingOff, TimeSpan.Zero);
        if (label is { Length: > MaxLabelLength })
        {
            throw new GatewayRuleException("INVALID_REQUEST", $"A wallet label can have at most {MaxLabelLength} characters.");
        }

        var existing = _wallets.LastOrDefault(wallet =>
            wallet.Network == network && wallet.Address == address && (wallet.IsPendingAt(now) || wallet.IsUsableAt(now)));
        if (existing is not null)
        {
            created = false;
            return existing;
        }

        if (_wallets.Any(wallet => wallet.Network == network && wallet.IsPendingAt(now)))
        {
            throw new GatewayRuleException(
                "WALLET_CHANGE_PENDING", $"Another {network} wallet is waiting to become active; disable it before registering a different one.");
        }

        var previous = Current(network, now);
        var status = screening.Risk == WalletRisk.Low ? SettlementWalletStatus.Approved : SettlementWalletStatus.UnderReview;
        var wallet = new SettlementWallet(walletId, ClientId, network, address, label, screening, now, now + coolingOff, status);
        wallet.Record(SettlementWalletActionKind.Registered, actor, null, now);
        if (status == SettlementWalletStatus.Approved && previous is not null)
        {
            previous.RetiredAt = wallet.UsableFrom;
        }

        _wallets.Add(wallet);
        created = true;
        return wallet;
    }

    /// <summary>A reviewer accepts a wallet under review. It becomes usable when its cooling-off period ends.</summary>
    public void Approve(Guid walletId, string reviewer, string reason, DateTimeOffset now)
    {
        var wallet = UnderReview(walletId);
        RequireReason(reason);
        if (wallet.Screening.Risk == WalletRisk.Blocked)
        {
            throw new GatewayRuleException("WALLET_BLOCKED", "A wallet on a sanctions or block list cannot be approved.");
        }

        var previous = Current(wallet.Network, now);
        wallet.Status = SettlementWalletStatus.Approved;
        wallet.Record(SettlementWalletActionKind.Approved, reviewer, reason, now);
        if (previous is not null)
        {
            previous.RetiredAt = wallet.UsableFrom > now ? wallet.UsableFrom : now;
        }
    }

    public void Reject(Guid walletId, string reviewer, string reason, DateTimeOffset now)
    {
        var wallet = UnderReview(walletId);
        RequireReason(reason);
        wallet.Status = SettlementWalletStatus.Rejected;
        wallet.Record(SettlementWalletActionKind.Rejected, reviewer, reason, now);
    }

    /// <summary>
    /// Withdraws a wallet. If it had not taken over yet, the wallet it would have replaced stays in use; if it had,
    /// the network has no wallet until a new one becomes usable.
    /// </summary>
    public void Disable(Guid walletId, string actor, string? reason, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        var wallet = Find(walletId) ?? throw new GatewayRuleException("WALLET_NOT_FOUND", "The wallet does not exist.");
        if (wallet.Status is SettlementWalletStatus.Rejected or SettlementWalletStatus.Disabled)
        {
            throw new GatewayRuleException("WALLET_ALREADY_CLOSED", "The wallet is already rejected or disabled.");
        }

        if (reason is { Length: > MaxReasonLength })
        {
            throw new GatewayRuleException("REASON_REQUIRED", $"A reason can have at most {MaxReasonLength} characters.");
        }

        var wasPending = wallet.IsPendingAt(now);
        wallet.Status = SettlementWalletStatus.Disabled;
        wallet.Record(SettlementWalletActionKind.Disabled, actor, reason, now);
        if (wasPending)
        {
            foreach (var replaced in _wallets.Where(other => other.Network == wallet.Network && other.RetiredAt > now))
            {
                replaced.RetiredAt = null;
            }
        }
    }

    /// <summary>An independent copy, for stores that keep settings in memory.</summary>
    public MerchantSettlement Copy() => new(ClientId, _wallets.Select(wallet => wallet.Copy()), Version);

    internal static MerchantSettlement Restore(string clientId, IEnumerable<SettlementWallet> wallets, int version) =>
        new(clientId, wallets, version);

    private static void RequireReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > MaxReasonLength)
        {
            throw new GatewayRuleException("REASON_REQUIRED", $"Every review decision needs a reason (at most {MaxReasonLength} characters).");
        }
    }

    private SettlementWallet UnderReview(Guid walletId)
    {
        var wallet = Find(walletId) ?? throw new GatewayRuleException("WALLET_NOT_FOUND", "The wallet does not exist.");
        return wallet.Status == SettlementWalletStatus.UnderReview
            ? wallet
            : throw new GatewayRuleException("WALLET_NOT_UNDER_REVIEW", "Only a wallet under review can be approved or rejected.");
    }
}
