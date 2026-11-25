using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using FiatCryptoGateway.Application.Ports;
using FiatCryptoGateway.Domain;
using FiatCryptoGateway.Domain.Assets;
using FiatCryptoGateway.Domain.Orders;
using FiatCryptoGateway.Domain.Pricing;

namespace FiatCryptoGateway.Sandbox;

/// <summary>
/// The sandbox catalogue: every circulating ISO 4217 currency, plus crypto assets. The stablecoins are the ones
/// chosen in docs/decisions.md (question 13): USDT on Tron, USDT and USDC on Polygon, Ethereum and Solana. Each
/// network's native coin is listed too, because network fees are paid in it.
/// </summary>
public static class SandboxAssets
{
    public static readonly AssetId Bitcoin = AssetId.Crypto("BTC", "bitcoin");
    public static readonly AssetId Ether = AssetId.Crypto("ETH", "ethereum");
    public static readonly AssetId UsdcEthereum = AssetId.Crypto("USDC", "ethereum");
    public static readonly AssetId UsdtEthereum = AssetId.Crypto("USDT", "ethereum");
    public static readonly AssetId Sol = AssetId.Crypto("SOL", "solana");
    public static readonly AssetId UsdcSolana = AssetId.Crypto("USDC", "solana");
    public static readonly AssetId UsdtSolana = AssetId.Crypto("USDT", "solana");
    public static readonly AssetId Tron = AssetId.Crypto("TRX", "tron");
    public static readonly AssetId UsdtTron = AssetId.Crypto("USDT", "tron");
    public static readonly AssetId Pol = AssetId.Crypto("POL", "polygon");
    public static readonly AssetId UsdtPolygon = AssetId.Crypto("USDT", "polygon");
    public static readonly AssetId UsdcPolygon = AssetId.Crypto("USDC", "polygon");

    public static IReadOnlyList<Asset> Crypto { get; } =
    [
        Asset.Crypto(Bitcoin, 8, "Bitcoin", Bitcoin),
        Asset.Crypto(Ether, 18, "Ether", Ether),
        Asset.Crypto(UsdcEthereum, 6, "USD Coin on Ethereum", Ether),
        Asset.Crypto(UsdtEthereum, 6, "Tether USD on Ethereum", Ether),
        Asset.Crypto(Sol, 9, "Solana", Sol),
        Asset.Crypto(UsdcSolana, 6, "USD Coin on Solana", Sol),
        Asset.Crypto(UsdtSolana, 6, "Tether USD on Solana", Sol),
        Asset.Crypto(Tron, 6, "Tron", Tron),
        Asset.Crypto(UsdtTron, 6, "Tether USD on Tron", Tron),
        Asset.Crypto(Pol, 18, "Polygon Ecosystem Token", Pol),
        Asset.Crypto(UsdtPolygon, 6, "Tether USD on Polygon", Pol),
        Asset.Crypto(UsdcPolygon, 6, "USD Coin on Polygon", Pol),
    ];

    /// <summary>The stablecoins merchants can be settled in (question 13).</summary>
    public static IReadOnlyList<AssetId> SettlementStablecoins { get; } =
        [UsdtTron, UsdtPolygon, UsdcPolygon, UsdtEthereum, UsdcEthereum, UsdcSolana, UsdtSolana];

    public static AssetCatalog CreateCatalog() => new(Iso4217.All.Concat(Crypto));
}

/// <summary>
/// Fixed, made-up prices for development. They are NOT market data and must never be used to quote a
/// real customer. Fiat currencies without a sandbox price return NO_PRICE_ROUTE, as they would without
/// an FX partner that covers them.
/// </summary>
public sealed class SandboxPriceFeed(TimeProvider time) : IPriceFeed
{
    public const string SourceName = "sandbox-static-prices";

    private static readonly AssetId Usd = AssetId.Fiat("USD");

    // US dollars per one unit. Illustrative values only.
    private static readonly Dictionary<AssetId, decimal> UsdPerUnit = new()
    {
        [Usd] = 1m,
        [AssetId.Fiat("EUR")] = 1.08m,
        [AssetId.Fiat("GBP")] = 1.27m,
        [AssetId.Fiat("INR")] = 0.0118m,
        [AssetId.Fiat("JPY")] = 0.0066m,
        [AssetId.Fiat("BRL")] = 0.18m,
        [AssetId.Fiat("NGN")] = 0.00065m,
        [AssetId.Fiat("KWD")] = 3.25m,
        [AssetId.Fiat("AED")] = 0.2723m,
        [AssetId.Fiat("SGD")] = 0.74m,
        [AssetId.Fiat("CAD")] = 0.72m,
        [AssetId.Fiat("AUD")] = 0.65m,
        [AssetId.Fiat("ZAR")] = 0.055m,
        [AssetId.Fiat("MXN")] = 0.058m,
        [SandboxAssets.Bitcoin] = 90_000m,
        [SandboxAssets.Ether] = 3_000m,
        [SandboxAssets.UsdcEthereum] = 1m,
        [SandboxAssets.UsdtEthereum] = 1m,
        [SandboxAssets.Sol] = 150m,
        [SandboxAssets.UsdcSolana] = 1m,
        [SandboxAssets.UsdtSolana] = 1m,
        [SandboxAssets.Tron] = 0.25m,
        [SandboxAssets.UsdtTron] = 1m,
        [SandboxAssets.Pol] = 0.4m,
        [SandboxAssets.UsdtPolygon] = 1m,
        [SandboxAssets.UsdcPolygon] = 1m,
    };

    public Task<IReadOnlyList<PriceObservation>> GetRouteAsync(AssetId source, AssetId destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        if (!UsdPerUnit.TryGetValue(source, out var sourceUsd) || !UsdPerUnit.TryGetValue(destination, out var destinationUsd))
        {
            throw new GatewayRuleException("NO_PRICE_ROUTE", $"The sandbox has no price route from {source} to {destination}.");
        }

        var observedAt = time.GetUtcNow() - TimeSpan.FromMilliseconds(500);
        var route = new List<PriceObservation>();
        if (source != Usd)
        {
            route.Add(new PriceObservation(source, Usd, sourceUsd, observedAt, observedAt, SourceName));
        }

        if (destination != Usd)
        {
            route.Add(new PriceObservation(Usd, destination, 1m / destinationUsd, observedAt, observedAt, SourceName));
        }

        if (route.Count == 0)
        {
            route.Add(PriceObservation.Identity(Usd, observedAt));
        }

        return Task.FromResult<IReadOnlyList<PriceObservation>>(route.AsReadOnly());
    }
}

/// <summary>Executes at the sandbox market price with no slippage. Idempotent per order.</summary>
public sealed class SandboxLiquidityProvider(IPriceFeed prices, TimeProvider time) : ILiquidityProvider
{
    private readonly ConcurrentDictionary<Guid, LiquidityExecution> _executions = new();

    public string Name => "sandbox-liquidity";

    public int ExecutionCount => _executions.Count;

    public async Task<LiquidityExecution> BuyAsync(PaymentOrder order, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);
        if (_executions.TryGetValue(order.OrderId, out var existing))
        {
            return existing;
        }

        var quote = order.Quote;
        var route = await prices.GetRouteAsync(quote.NetSourceAmount.Asset.Id, quote.DestinationAmount.Asset.Id, cancellationToken);
        var marketRate = route.Aggregate(1m, (rate, observation) => rate * observation.Rate);
        var received = Money.RoundDown(quote.NetSourceAmount.Amount * marketRate, quote.DestinationAmount.Asset);
        var execution = new LiquidityExecution(
            Name, $"lpx_{order.OrderId:N}", order.OrderId, quote.NetSourceAmount, received, time.GetUtcNow());
        return _executions.GetOrAdd(order.OrderId, execution);
    }
}

/// <summary>
/// Pretends to hold funds and broadcast transactions. Address checks are format-only; a real custodian
/// also verifies checksums. Idempotent per order: a retried send returns the original transfer.
/// </summary>
public sealed partial class SandboxCustodyProvider(AssetCatalog catalog, TimeProvider time) : ICustodyProvider
{
    private readonly ConcurrentDictionary<Guid, CryptoTransfer> _transfers = new();

    public string Name => "sandbox-custody";

    public int TransferCount => _transfers.Count;

    public bool IsValidAddress(Asset asset, string address)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (string.IsNullOrEmpty(address))
        {
            return false;
        }

        // Polygon is EVM-compatible, so its addresses look like Ethereum's.
        return asset.Id.Network switch
        {
            "bitcoin" => BitcoinAddress().IsMatch(address),
            "ethereum" or "polygon" => EthereumAddress().IsMatch(address),
            "solana" => SolanaAddress().IsMatch(address),
            "tron" => TronAddress().IsMatch(address),
            _ => false,
        };
    }

    /// <summary>Sandbox confirmation depths; the real values are a risk decision per network.</summary>
    public int RequiredConfirmations(Asset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        return asset.Id.Network switch
        {
            "bitcoin" => 3,
            "ethereum" => 12,
            "solana" => 1,
            "tron" => 20,
            "polygon" => 64,
            _ => throw new GatewayRuleException("UNSUPPORTED_ASSET", $"{asset.Id} has no confirmation policy."),
        };
    }

    public Task<Money> EstimateCustomerNetworkFeeAsync(Asset asset, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var fee = asset.Id.ToString() switch
        {
            "BTC@bitcoin" => 0.00002m,
            "ETH@ethereum" => 0.0005m,
            "USDC@ethereum" or "USDT@ethereum" => 1.5m,
            "SOL@solana" => 0.00001m,
            "USDC@solana" or "USDT@solana" => 0.01m,
            "TRX@tron" or "USDT@tron" => 1m,
            "POL@polygon" => 0.01m,
            "USDC@polygon" or "USDT@polygon" => 0.05m,
            _ => throw new GatewayRuleException("UNSUPPORTED_ASSET", $"The sandbox has no network fee for {asset.Id}."),
        };
        return Task.FromResult(new Money(fee, asset));
    }

    public Task<CryptoTransfer> SendAsync(PaymentOrder order, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);
        return Task.FromResult(_transfers.GetOrAdd(order.OrderId, _ => CreateTransfer(order)));
    }

    private CryptoTransfer CreateTransfer(PaymentOrder order)
    {
        var asset = order.Quote.DestinationAmount.Asset;
        var feeAsset = catalog.Get(asset.NetworkFeeAsset ?? throw new InvalidOperationException("A crypto asset needs a fee asset."));
        var gasPaid = feeAsset.Id.Network switch
        {
            "bitcoin" => 0.000015m,
            "ethereum" => 0.0004m,
            "tron" => 15m,
            "polygon" => 0.005m,
            _ => 0.000005m,
        };
        var hash = Convert.ToHexStringLower(SHA256.HashData(order.OrderId.ToByteArray()));
        return new CryptoTransfer(
            Name,
            $"sandboxtx_{hash}",
            order.OrderId,
            order.Quote.DestinationAmount,
            order.DestinationAddress,
            new Money(gasPaid, feeAsset),
            time.GetUtcNow());
    }

    [GeneratedRegex("^(bc1[02-9ac-hj-np-z]{11,71}|[13][1-9A-HJ-NP-Za-km-z]{25,34})$", RegexOptions.CultureInvariant)]
    private static partial Regex BitcoinAddress();

    [GeneratedRegex("^0x[0-9a-fA-F]{40}$", RegexOptions.CultureInvariant)]
    private static partial Regex EthereumAddress();

    [GeneratedRegex("^[1-9A-HJ-NP-Za-km-z]{32,44}$", RegexOptions.CultureInvariant)]
    private static partial Regex SolanaAddress();

    // Base58, 34 characters, always starting with T. Format only: a real custodian also checks the checksum.
    [GeneratedRegex("^T[1-9A-HJ-NP-Za-km-z]{33}$", RegexOptions.CultureInvariant)]
    private static partial Regex TronAddress();
}
