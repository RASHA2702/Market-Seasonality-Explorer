using FiatCryptoGateway.Api.Auth;
using FiatCryptoGateway.Application.Ports;
using FiatCryptoGateway.Domain.Assets;
using FiatCryptoGateway.Persistence.Postgres;
using FiatCryptoGateway.Sandbox;
using Microsoft.Extensions.Options;

namespace FiatCryptoGateway.Api;

public sealed class StorageOptions
{
    public const string SectionName = "Gateway:Storage";

    /// <summary><c>InMemory</c> (default; lost on restart, one instance only) or <c>Postgres</c>.</summary>
    public string Provider { get; set; } = "InMemory";

    /// <summary>Apply pending migrations at startup. Safe with several instances (they take turns).</summary>
    public bool ApplyMigrationsOnStartup { get; set; } = true;
}

public static class StorageServiceCollectionExtensions
{
    public static IServiceCollection AddGatewayStorage(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var options = configuration.GetSection(StorageOptions.SectionName).Get<StorageOptions>() ?? new StorageOptions();

        switch (options.Provider)
        {
            case "InMemory":
                services.AddInMemoryStores();
                break;
            case "Postgres":
                var connectionString = configuration.GetConnectionString("Gateway");
                if (string.IsNullOrWhiteSpace(connectionString))
                {
                    throw new InvalidOperationException(
                        "Gateway:Storage:Provider is Postgres, so ConnectionStrings:Gateway must be set " +
                        "(for example with the ConnectionStrings__Gateway environment variable).");
                }

                services.AddPostgresStores(connectionString, options.ApplyMigrationsOnStartup);
                break;
            default:
                throw new InvalidOperationException($"Gateway:Storage:Provider must be InMemory or Postgres, not '{options.Provider}'.");
        }

        // Registered before the order worker, so storage is ready before any processing starts.
        services.AddHostedService<StorageInitializationService>();
        return services;
    }
}

/// <summary>Runs once at startup: migrations, then reference data the database needs.</summary>
public sealed class StorageInitializationService(
    IStorageInitializer initializer,
    AssetCatalog catalog,
    IOptions<ApiKeyOptions> apiKeys) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) =>
        initializer.InitializeAsync(
            catalog.Assets,
            apiKeys.Value.Clients.Select(client => new ApiClientRecord(client.ClientId, client.KeySha256)),
            cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
