using System.Security.Cryptography;
using System.Text;
using FiatCryptoGateway.Application.OnRamp;
using FiatCryptoGateway.Application.Settlement;
using FiatCryptoGateway.Domain.Orders;
using FiatCryptoGateway.Domain.Settlement;
using FiatCryptoGateway.Sandbox;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FiatCryptoGateway.Persistence.Tests;

/// <summary>Real orders in the shared database, for payments to refer to.</summary>
internal static class PostgresOrders
{
    public const string Address = "0x1111111111111111111111111111111111111111";

    /// <summary>A new order from a merchant of its own, with an approved settlement wallet.</summary>
    public static async Task<PaymentOrder> CreateAsync(ServiceProvider services, decimal amount = 10_000m, string currency = "INR", string wallet = Address)
    {
        var merchant = $"merchant-{Guid.NewGuid():N}";
        await using (var connection = await services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync())
        await using (var command = new NpgsqlCommand(
            "INSERT INTO gateway.api_client (client_id, display_name, key_sha256, role) VALUES (@id, @id, @hash, 'MERCHANT')", connection))
        {
            command.Parameters.AddWithValue("id", merchant);
            command.Parameters.AddWithValue("hash", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(merchant))));
            await command.ExecuteNonQueryAsync();
        }

        var settlement = services.GetRequiredService<SettlementService>();
        var (registered, _) = await settlement.RegisterAsync(merchant, "ethereum", wallet, null, CancellationToken.None);
        if (registered.Status == SettlementWalletStatus.UnderReview)
        {
            await settlement.ApproveAsync(registered.WalletId, "officer", "Test wallet, approved for this scenario.", CancellationToken.None);
        }

        var onRamp = services.GetRequiredService<OnRampService>();
        var quote = await onRamp.CreateQuoteAsync(merchant, new CreateQuoteCommand(currency, amount, "USDC@ethereum"), CancellationToken.None);
        return await onRamp.CreateOrderAsync(merchant, new CreateOrderCommand(quote.QuoteId, SandboxCustomerDirectory.Verified), CancellationToken.None);
    }
}
