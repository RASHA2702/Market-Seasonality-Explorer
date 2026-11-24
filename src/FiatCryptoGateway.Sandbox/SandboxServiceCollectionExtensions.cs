using FiatCryptoGateway.Application.Ports;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FiatCryptoGateway.Sandbox;

public static class SandboxServiceCollectionExtensions
{
    /// <summary>
    /// Registers simulated providers and in-memory stores. Nothing here can move real money: there is no
    /// network call to any bank, exchange, custodian or KYC vendor.
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

        services.AddSingleton<IQuoteStore, InMemoryQuoteStore>();
        services.AddSingleton<IOrderStore, InMemoryOrderStore>();
        services.AddSingleton<ILedgerStore, InMemoryLedgerStore>();
        services.AddSingleton<IIdempotencyStore, InMemoryIdempotencyStore>();
        services.AddSingleton<IWebhookInbox, InMemoryWebhookInbox>();

        services.AddSingleton<SandboxFiatWebhookHandler>();
        return services;
    }
}
