using System.Text.Json.Serialization;
using FiatCryptoGateway.Api.Auth;
using FiatCryptoGateway.Application;
using FiatCryptoGateway.Sandbox;

namespace FiatCryptoGateway.Api;

public static class GatewayApiServiceCollectionExtensions
{
    /// <summary>
    /// Everything the API registers, in one place, so tests can check the settings rules on exactly what the
    /// API runs. Settings are validated when the host starts: anything unsafe stops the process.
    /// </summary>
    public static IServiceCollection AddGatewayApi(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // Validates Gateway:Mode at startup: anything other than Sandbox stops the process.
        services.AddGatewayCore(configuration);
        services.AddSandboxProviders(configuration);
        services.AddGatewayStorage(configuration);
        services.AddApiKeyAuthentication(configuration);
        services.AddHealthChecks();
        services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.NumberHandling = JsonNumberHandling.AllowReadingFromString;
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
        });
        services.AddOptions<ProcessingOptions>().Bind(configuration.GetSection(ProcessingOptions.SectionName));
        services.AddHostedService<OrderProcessingWorker>();
        services.AddMerchantWebhookDelivery();
        services.AddHostedService<ReconciliationWorker>();
        services.AddOptions<Review.ReviewConsoleOptions>().Bind(configuration.GetSection(Review.ReviewConsoleOptions.SectionName));
        return services;
    }
}
