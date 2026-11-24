using FiatCryptoGateway.Application.OnRamp;
using FiatCryptoGateway.Application.Ports;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FiatCryptoGateway.Sandbox;

public static class SandboxServiceCollectionExtensions
{
    /// <summary>
    /// Registers simulated providers. Nothing here can move real money: there is no network call to any
    /// bank, exchange, custodian or KYC vendor. Storage is registered separately.
    /// </summary>
    public static IServiceCollection AddSandboxProviders(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<SandboxOptions>().Bind(configuration.GetSection(SandboxOptions.SectionName));
        services.AddSingleton(_ => SandboxAssets.CreateCatalog());
        services.AddSingleton<SandboxWebhookSecret>();

        services.AddSingleton<IPriceFeed, SandboxPriceFeed>();
        services.AddSingleton<SandboxFiatProvider>();
        services.AddSingleton<IFiatPaymentProvider>(provider => provider.GetRequiredService<SandboxFiatProvider>());
        services.AddSingleton<SandboxLiquidityProvider>();
        services.AddSingleton<ILiquidityProvider>(provider => provider.GetRequiredService<SandboxLiquidityProvider>());
        services.AddSingleton<SandboxCustodyProvider>();
        services.AddSingleton<ICustodyProvider>(provider => provider.GetRequiredService<SandboxCustodyProvider>());
        services.AddSingleton<ICustomerDirectory, SandboxCustomerDirectory>();
        services.AddSingleton<IWalletScreening, SandboxWalletScreening>();

        services.AddSingleton<SandboxFiatWebhookHandler>();
        return services;
    }

    /// <summary>Process-memory storage: fast and dependency-free, lost on restart, single instance only.</summary>
    public static IServiceCollection AddInMemoryStores(this IServiceCollection services)
    {
        services.AddSingleton<InMemoryLedgerStore>();
        services.AddSingleton<ILedgerStore>(provider => provider.GetRequiredService<InMemoryLedgerStore>());
        services.AddSingleton<InMemoryOutbox>();
        services.AddSingleton<IOutbox>(provider => provider.GetRequiredService<InMemoryOutbox>());
        services.AddSingleton<IQuoteStore, InMemoryQuoteStore>();
        services.AddSingleton<IOrderStore, InMemoryOrderStore>();
        services.AddSingleton<IIdempotencyStore, InMemoryIdempotencyStore>();
        services.AddSingleton<IWebhookInbox, InMemoryWebhookInbox>();
        services.AddSingleton<IOrderLocks, InMemoryOrderLocks>();
        services.AddSingleton<IStorageInitializer, InMemoryStorageInitializer>();
        return services;
    }
}
