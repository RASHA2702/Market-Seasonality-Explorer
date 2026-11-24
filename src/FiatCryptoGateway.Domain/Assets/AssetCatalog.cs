using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace FiatCryptoGateway.Domain.Assets;

/// <summary>The assets this deployment supports. Anything not in the catalog is rejected.</summary>
public sealed class AssetCatalog
{
    private readonly Dictionary<AssetId, Asset> _assets = [];

    public AssetCatalog(IEnumerable<Asset> assets)
    {
        ArgumentNullException.ThrowIfNull(assets);
        foreach (var asset in assets)
        {
            if (!_assets.TryAdd(asset.Id, asset))
            {
                throw new ArgumentException($"Duplicate asset {asset.Id}.", nameof(assets));
            }
        }
    }

    public IReadOnlyCollection<Asset> Assets => _assets.Values;

    public bool TryGet(AssetId id, [NotNullWhen(true)] out Asset? asset) => _assets.TryGetValue(id, out asset);

    public Asset Get(AssetId id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return TryGet(id, out var asset)
            ? asset
            : throw new GatewayRuleException("UNSUPPORTED_ASSET", $"{id} is not supported.");
    }
}

/// <summary>
/// Every circulating ISO 4217 currency with its official minor units, loaded from the embedded
/// <c>iso4217.csv</c> (source and date are recorded in that file).
/// </summary>
public static class Iso4217
{
    private static readonly Lazy<IReadOnlyList<Asset>> Currencies = new(Load);

    public static IReadOnlyList<Asset> All => Currencies.Value;

    private static ReadOnlyCollection<Asset> Load()
    {
        const string resource = "FiatCryptoGateway.Domain.Assets.iso4217.csv";
        using var stream = typeof(Iso4217).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Embedded resource {resource} is missing.");
        using var reader = new StreamReader(stream);

        var currencies = new List<Asset>();
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("code,", StringComparison.Ordinal))
            {
                continue;
            }

            var fields = line.Split(',', 4);
            currencies.Add(Asset.Fiat(fields[0], int.Parse(fields[2], CultureInfo.InvariantCulture), fields[3].Trim('"')));
        }

        return currencies.AsReadOnly();
    }
}
