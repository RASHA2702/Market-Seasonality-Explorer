using FiatCryptoGateway.Application.Ports;
using FiatCryptoGateway.Domain.Assets;
using FiatCryptoGateway.Domain.Compliance;

namespace FiatCryptoGateway.Sandbox;

/// <summary>
/// Seeded test customers, available to every API client in the sandbox. A real deployment reads KYC
/// status from its KYC provider and stores only references, never identity documents.
/// </summary>
public sealed class SandboxCustomerDirectory : ICustomerDirectory
{
    public const string Verified = "cust_verified";
    public const string BasicKyc = "cust_basic";
    public const string Unverified = "cust_unverified";
    public const string SanctionsMatch = "cust_sanctioned";
    public const string BlockedCountry = "cust_blocked_country";
    public const string Foreign = "cust_foreign";

    // Indian residents, matching the India-only launch, plus two outside India to exercise the country rule.
    private static readonly Dictionary<string, (KycLevel Level, string Country, bool Sanctions)> Customers = new(StringComparer.Ordinal)
    {
        [Verified] = (KycLevel.Full, "IN", false),
        [BasicKyc] = (KycLevel.Basic, "IN", false),
        [Unverified] = (KycLevel.None, "IN", false),
        [SanctionsMatch] = (KycLevel.Full, "IN", true),
        [BlockedCountry] = (KycLevel.Full, "KP", false),
        [Foreign] = (KycLevel.Full, "GB", false),
    };

    public Task<CustomerProfile?> FindAsync(string clientId, string customerId, CancellationToken cancellationToken) =>
        Task.FromResult(
            Customers.TryGetValue(customerId, out var customer)
                ? new CustomerProfile(clientId, customerId, customer.Level, customer.Country, customer.Sanctions)
                : null);
}

/// <summary>
/// Sandbox wallet screening. Three fixed Ethereum addresses trigger the interesting outcomes;
/// every other address screens as low risk.
/// </summary>
public sealed class SandboxWalletScreening : IWalletScreening
{
    public const string BlockedAddress = "0x5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a";
    public const string HighRiskAddress = "0x7e7e7e7e7e7e7e7e7e7e7e7e7e7e7e7e7e7e7e7e";

    /// <summary>Simulates the screening provider being down.</summary>
    public const string OutageAddress = "0xfa11fa11fa11fa11fa11fa11fa11fa11fa11fa11";

    private const string Screener = "sandbox-wallet-screening";

    public Task<WalletScreeningResult> ScreenAsync(Asset asset, string address, CancellationToken cancellationToken)
    {
        if (string.Equals(address, OutageAddress, StringComparison.OrdinalIgnoreCase))
        {
            throw new TimeoutException("The sandbox wallet-screening provider did not respond.");
        }

        var risk = string.Equals(address, BlockedAddress, StringComparison.OrdinalIgnoreCase) ? WalletRisk.Blocked
            : string.Equals(address, HighRiskAddress, StringComparison.OrdinalIgnoreCase) ? WalletRisk.High
            : WalletRisk.Low;
        return Task.FromResult(new WalletScreeningResult(risk, Screener, null));
    }
}
