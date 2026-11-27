using FiatCryptoGateway.Application.OnRamp;
using FiatCryptoGateway.Application.Payments;
using FiatCryptoGateway.Application.Ports;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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

        // Two ways to collect rupees, chosen by Gateway:Fiat:Provider: the gateway's own processor (the default) or the
        // earlier simulated partner. Only the chosen one is the order flow's provider and the reconciled report source.
        static bool UsesPartner(IServiceProvider provider) =>
            provider.GetRequiredService<IOptions<FiatProviderOptions>>().Value.Provider == FiatProviderChoice.SandboxPartner;

        services.AddSingleton<IFiatPaymentProvider>(provider =>
            UsesPartner(provider) ? provider.GetRequiredService<SandboxFiatProvider>() : provider.GetRequiredService<GatewayProcessorProvider>());
        services.AddSingleton<ISettlementReportSource>(provider =>
            UsesPartner(provider) ? provider.GetRequiredService<SandboxFiatProvider>() : provider.GetRequiredService<GatewayProcessorProvider>());

        // Simulated payment networks, one per payment method. Test instruments only.
        services.AddSingleton<IPaymentRail, SandboxCardRail>();
        services.AddSingleton<IPaymentRail, SandboxUpiRail>();
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
        services.AddSingleton<IReconciliationStore, InMemoryReconciliationStore>();
        services.AddSingleton<ISettlementStore, InMemorySettlementStore>();
        services.AddSingleton<Application.Reviewers.IReviewerAccessStore, InMemoryReviewerAccessStore>();
        services.AddSingleton<Application.Alerts.IBackupRunStore, InMemoryBackupRunStore>();
        services.AddSingleton<Application.Payments.IPaymentAttemptStore, InMemoryPaymentAttemptStore>();
        services.AddSingleton<Application.Alerts.IAlertLog, InMemoryAlertLog>();
        services.AddSingleton<IStorageInitializer, InMemoryStorageInitializer>();
        return services;
    }
}
