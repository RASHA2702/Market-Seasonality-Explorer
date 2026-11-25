using System.Text.RegularExpressions;
using FiatCryptoGateway.Application.OnRamp;
using FiatCryptoGateway.Application.Ports;
using FiatCryptoGateway.Domain;
using FiatCryptoGateway.Domain.Assets;
using FiatCryptoGateway.Domain.Compliance;
using FiatCryptoGateway.Domain.Settlement;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FiatCryptoGateway.Application.Settlement;

public sealed partial class SettlementOptions
{
    public const string SectionName = "Gateway:Settlement";

    /// <summary>
    /// Hours a new wallet waits before it can receive payments, so a merchant can notice and disable a wallet it did
    /// not register (for example one added with a stolen API key).
    /// </summary>
    public int CoolingOffHours { get; set; } = 24;

    /// <summary>Networks merchants can be paid on, comma-separated. Defaults to the networks chosen in question 13.</summary>
    public string Networks { get; set; } = "tron,polygon,ethereum,solana";

    public IReadOnlyList<string> NetworkList =>
        [.. Networks.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(network => network.ToLowerInvariant()).Distinct(StringComparer.Ordinal)];

    [GeneratedRegex("^[a-z0-9-]{2,32}$", RegexOptions.CultureInvariant)]
    internal static partial Regex NetworkName();
}

public sealed class SettlementOptionsValidator(AssetCatalog catalog) : IValidateOptions<SettlementOptions>
{
    public ValidateOptionsResult Validate(string? name, SettlementOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        if (options.CoolingOffHours is < 0 or > 720)
        {
            failures.Add("Settlement CoolingOffHours must be between 0 and 720.");
        }

        if (options.NetworkList.Count == 0)
        {
            failures.Add("Settlement Networks must name at least one network.");
        }

        foreach (var network in options.NetworkList)
        {
            if (!SettlementOptions.NetworkName().IsMatch(network) || SettlementService.NativeAsset(catalog, network) is null)
            {
                failures.Add($"Settlement network '{network}' is not a network in the asset catalogue.");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

/// <summary>What a merchant may see of a wallet's state; screening detail stays with reviewers.</summary>
public static class SettlementWalletView
{
    public static string Status(SettlementWallet wallet, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(wallet);
        return wallet.Status switch
        {
            SettlementWalletStatus.UnderReview => "UnderReview",
            SettlementWalletStatus.Rejected => "NotAccepted",
            SettlementWalletStatus.Disabled => "Disabled",
            _ when now < wallet.UsableFrom => "CoolingOff",
            _ when wallet.RetiredAt <= now => "Replaced",
            _ => "Active",
        };
    }
}

/// <summary>Registers, screens, reviews and withdraws the wallets merchants are paid into.</summary>
public sealed partial class SettlementService(
    ISettlementStore store,
    IWalletScreening screening,
    OnRampProviders providers,
    AssetCatalog catalog,
    IOrderLocks locks,
    IOptions<SettlementOptions> options,
    TimeProvider time,
    ILogger<SettlementService> logger)
{
    private const string ScreeningUnavailable = "screening-unavailable";

    public IReadOnlyList<string> Networks => options.Value.NetworkList;

    /// <summary>The network's own coin (the asset that pays its fees), if the catalogue has the network.</summary>
    public static Asset? NativeAsset(AssetCatalog catalog, string network)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.Assets.FirstOrDefault(asset => asset.Id.Network == network && asset.NetworkFeeAsset == asset.Id);
    }

    /// <summary>
    /// Registers a wallet after checking its format and screening it. A clean wallet becomes usable after the
    /// cooling-off period; anything else waits for a reviewer. Registering the same wallet again returns it.
    /// </summary>
    public async Task<(SettlementWallet Wallet, bool Created)> RegisterAsync(
        string clientId,
        string? network,
        string? address,
        string? label,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        var normalisedNetwork = network?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(normalisedNetwork) || !Networks.Contains(normalisedNetwork, StringComparer.Ordinal))
        {
            throw new GatewayRuleException(
                "UNSUPPORTED_NETWORK", $"Merchants can be paid on these networks: {string.Join(", ", Networks)}.");
        }

        var native = NativeAsset(catalog, normalisedNetwork)
            ?? throw new GatewayRuleException("UNSUPPORTED_NETWORK", $"The {normalisedNetwork} network is not available.");
        var trimmed = address?.Trim();
        if (string.IsNullOrEmpty(trimmed) || !providers.Custody.IsValidAddress(native, trimmed))
        {
            throw new GatewayRuleException("INVALID_WALLET_ADDRESS", $"Not a valid {normalisedNetwork} address.");
        }

        var cleanLabel = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
        await using (await locks.AcquireAsync(OrderLockKeys.Settlement(clientId), cancellationToken))
        {
            var settlement = await store.GetAsync(clientId, cancellationToken);
            var now = time.GetUtcNow();
            var known = settlement.Wallets.LastOrDefault(wallet =>
                wallet.Network == normalisedNetwork && wallet.Address == trimmed && (wallet.IsPendingAt(now) || wallet.IsUsableAt(now)));
            if (known is not null)
            {
                return (known, false);
            }

            var result = await ScreenAsync(native, trimmed, cancellationToken);
            var registered = settlement.Register(
                Guid.NewGuid(),
                normalisedNetwork,
                trimmed,
                cleanLabel,
                result,
                actor: clientId,
                now,
                TimeSpan.FromHours(options.Value.CoolingOffHours),
                out var created);
            if (created)
            {
                await store.SaveAsync(settlement, cancellationToken);
            }

            return (registered, created);
        }
    }

    public async Task<IReadOnlyList<SettlementWallet>> ListAsync(string clientId, CancellationToken cancellationToken)
    {
        var settlement = await store.GetAsync(clientId, cancellationToken);
        return [.. settlement.Wallets.Reverse()];
    }

    /// <summary>The wallet that receives the merchant's payments on <paramref name="network"/> now, if any.</summary>
    public async Task<SettlementWallet?> CurrentAsync(string clientId, string network, CancellationToken cancellationToken) =>
        (await store.GetAsync(clientId, cancellationToken)).Current(network, time.GetUtcNow());

    /// <summary>The merchant withdraws one of its own wallets.</summary>
    public Task<SettlementWallet> DisableByMerchantAsync(string clientId, Guid walletId, CancellationToken cancellationToken) =>
        ChangeAsync(clientId, walletId, (settlement, now) => settlement.Disable(walletId, clientId, null, now), cancellationToken);

    public Task<IReadOnlyList<SettlementWallet>> ListForReviewAsync(CancellationToken cancellationToken) =>
        store.ListUnderReviewAsync(cancellationToken);

    public async Task<SettlementWallet> GetForReviewAsync(Guid walletId, CancellationToken cancellationToken)
    {
        var owner = await OwnerAsync(walletId, cancellationToken);
        return (await store.GetAsync(owner, cancellationToken)).Find(walletId)!;
    }

    public async Task<SettlementWallet> ApproveAsync(Guid walletId, string reviewer, string? reason, CancellationToken cancellationToken) =>
        await ChangeAsync(
            await OwnerAsync(walletId, cancellationToken),
            walletId,
            (settlement, now) => settlement.Approve(walletId, reviewer, reason ?? string.Empty, now),
            cancellationToken);

    public async Task<SettlementWallet> RejectAsync(Guid walletId, string reviewer, string? reason, CancellationToken cancellationToken) =>
        await ChangeAsync(
            await OwnerAsync(walletId, cancellationToken),
            walletId,
            (settlement, now) => settlement.Reject(walletId, reviewer, reason ?? string.Empty, now),
            cancellationToken);

    /// <summary>A reviewer stops a wallet being used, for example after new risk information. A reason is required.</summary>
    public async Task<SettlementWallet> DisableByReviewerAsync(Guid walletId, string reviewer, string? reason, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new GatewayRuleException("REASON_REQUIRED", "Every review decision needs a reason.");
        }

        return await ChangeAsync(
            await OwnerAsync(walletId, cancellationToken), walletId, (settlement, now) => settlement.Disable(walletId, reviewer, reason, now), cancellationToken);
    }

    private async Task<SettlementWallet> ChangeAsync(
        string clientId,
        Guid walletId,
        Action<MerchantSettlement, DateTimeOffset> change,
        CancellationToken cancellationToken)
    {
        await using (await locks.AcquireAsync(OrderLockKeys.Settlement(clientId), cancellationToken))
        {
            var settlement = await store.GetAsync(clientId, cancellationToken);
            var wallet = settlement.Find(walletId) ?? throw new ResourceNotFoundException("WALLET_NOT_FOUND", "The wallet does not exist.");
            change(settlement, time.GetUtcNow());
            await store.SaveAsync(settlement, cancellationToken);
            return wallet;
        }
    }

    private async Task<string> OwnerAsync(Guid walletId, CancellationToken cancellationToken) =>
        await store.FindOwnerAsync(walletId, cancellationToken)
            ?? throw new ResourceNotFoundException("WALLET_NOT_FOUND", "The wallet does not exist.");

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Fail closed: if screening cannot finish, the wallet waits for a reviewer instead of being approved.")]
    private async Task<WalletScreeningResult> ScreenAsync(Asset network, string address, CancellationToken cancellationToken)
    {
        try
        {
            return await screening.ScreenAsync(network, address, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogScreeningFailed(logger, network.Id.Network ?? "?", exception);
            return new WalletScreeningResult(WalletRisk.Unknown, ScreeningUnavailable, null);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Wallet screening failed for a {Network} settlement wallet; it will wait for a reviewer.")]
    private static partial void LogScreeningFailed(ILogger logger, string network, Exception exception);
}
