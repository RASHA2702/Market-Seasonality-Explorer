using FiatCryptoGateway.Application.Compliance;
using FiatCryptoGateway.Application.Configuration;
using FiatCryptoGateway.Application.OnRamp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace FiatCryptoGateway.Application;

public static class GatewayServiceCollectionExtensions
{
    /// <summary>
    /// Registers the gateway's core services and validates configuration at startup, including the
    /// sandbox-only safety gate. Provider and store implementations are registered separately.
    /// </summary>
    public static IServiceCollection AddGatewayCore(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<GatewaySafetyOptions>()
            .Bind(configuration.GetSection(GatewaySafetyOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<GatewaySafetyOptions>, GatewaySafetyGate>();

        // Real provider adapters register a ProviderEnvironmentProfile; the guard refuses live credentials.
        services.AddOptions<ProviderConnectionsOptions>()
            .Bind(configuration.GetSection(GatewaySafetyOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<ProviderConnectionsOptions>, LiveCredentialGuard>();

        services.AddOptions<PricingOptions>()
            .Bind(configuration.GetSection(PricingOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<PricingOptions>, PricingOptionsValidator>();

        services.AddOptions<ComplianceOptions>()
            .Bind(configuration.GetSection(ComplianceOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<ComplianceOptions>, ComplianceOptionsValidator>();

        // Stores and IOrderLocks are registered by the chosen storage (in memory or PostgreSQL).
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ComplianceScreeningService>();
        services.AddSingleton(provider => new OnRampProviders(
            provider.GetRequiredService<Ports.IPriceFeed>(),
            provider.GetRequiredService<Ports.IFiatPaymentProvider>(),
            provider.GetRequiredService<Ports.ILiquidityProvider>(),
            provider.GetRequiredService<Ports.ICustodyProvider>()));
        services.AddSingleton(provider => new OnRampStores(
            provider.GetRequiredService<Ports.IQuoteStore>(),
            provider.GetRequiredService<Ports.IOrderStore>(),
            provider.GetRequiredService<Ports.ILedgerStore>()));
        services.AddSingleton<OnRampService>();
        services.AddSingleton<ComplianceReviewService>();
        services.AddSingleton<IValidateOptions<Checkout.CheckoutOptions>, Checkout.CheckoutOptionsValidator>();
        services.AddOptions<Checkout.CheckoutOptions>().Bind(configuration.GetSection(Checkout.CheckoutOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<Checkout.CheckoutLinks>();
        services.AddSingleton<Checkout.CheckoutService>();
        services.AddSingleton<IValidateOptions<Settlement.SettlementOptions>, Settlement.SettlementOptionsValidator>();
        services.AddOptions<Settlement.SettlementOptions>()
            .Bind(configuration.GetSection(Settlement.SettlementOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<Settlement.SettlementService>();
        services.AddSingleton<IValidateOptions<Reconciliation.ReconciliationOptions>, Reconciliation.ReconciliationOptionsValidator>();
        services.AddOptions<Reconciliation.ReconciliationOptions>()
            .Bind(configuration.GetSection(Reconciliation.ReconciliationOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<Reconciliation.ReconciliationService>();
        services.AddSingleton<IValidateOptions<Webhooks.WebhookOptions>, Webhooks.WebhookOptionsValidator>();
        services.AddOptions<Webhooks.WebhookOptions>().Bind(configuration.GetSection(Webhooks.WebhookOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<Webhooks.MerchantWebhookDispatcher>();
        services.AddSingleton<IValidateOptions<Reviewers.ReviewerAccessOptions>, Reviewers.ReviewerAccessOptionsValidator>();
        services.AddOptions<Reviewers.ReviewerAccessOptions>()
            .Bind(configuration.GetSection(Reviewers.ReviewerAccessOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<Reviewers.ReviewerAccessService>();
        services.AddSingleton<IValidateOptions<Alerts.AlertOptions>, Alerts.AlertOptionsValidator>();
        services.AddOptions<Alerts.AlertOptions>().Bind(configuration.GetSection(Alerts.AlertOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<Alerts.AlertChecks>();
        services.AddSingleton<Alerts.IAlertSource>(provider => provider.GetRequiredService<Alerts.AlertChecks>());
        services.AddSingleton<Alerts.AlertService>();
        return services;
    }
}
