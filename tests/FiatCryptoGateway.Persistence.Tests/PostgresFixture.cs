using System.Security.Cryptography;
using System.Text;
using FiatCryptoGateway.Application;
using FiatCryptoGateway.Application.Ports;
using FiatCryptoGateway.Domain.Assets;
using FiatCryptoGateway.Persistence.Postgres;
using FiatCryptoGateway.Sandbox;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;

namespace FiatCryptoGateway.Persistence.Tests;

public sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>
/// One real PostgreSQL 17.6 server (in Docker) for the whole test run, migrated with the same embedded
/// migrations the API applies. Each test builds its own "API instance" (service provider) against it.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    public const string ClientId = "client-a";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17.6").Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await using var services = BuildServices(TimeProvider.System);
        var keyHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("test-only-key")));
        await services.GetRequiredService<IStorageInitializer>().InitializeAsync(
            services.GetRequiredService<AssetCatalog>().Assets,
            [new ApiClientRecord(ClientId, keyHash, ApiClientRole.Merchant)],
            CancellationToken.None);
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    /// <summary>A separate gateway instance: its own connection pool and its own sandbox providers.</summary>
    public ServiceProvider BuildServices(TimeProvider time)
    {
        // New settlement wallets are usable at once here; the cooling-off period has its own tests.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Gateway:Settlement:CoolingOffHours"] = "0" })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton(time);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddGatewayCore(configuration);
        services.AddSandboxProviders(configuration);
        services.AddPostgresStores(ConnectionString, applyMigrationsOnStartup: true);
        return services.BuildServiceProvider(validateScopes: true);
    }
}

[CollectionDefinition(Name)]
public sealed class SharedPostgres : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
