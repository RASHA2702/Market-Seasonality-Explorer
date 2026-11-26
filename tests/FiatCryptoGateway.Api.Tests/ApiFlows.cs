using System.Net;
using System.Net.Http.Json;
using FiatCryptoGateway.Api.Contracts;
using FiatCryptoGateway.Sandbox;

namespace FiatCryptoGateway.Api.Tests;

/// <summary>Steps several API tests share, done over HTTP as a merchant or reviewer would.</summary>
internal static class ApiFlows
{
    public const string Address = "0x1111111111111111111111111111111111111111";

    /// <summary>Makes <paramref name="wallet"/> the merchant's active Ethereum wallet, then creates a 10,000 INR order.</summary>
    public static async Task<OrderResponse> CreateOrderAsync(HttpClient merchant, HttpClient? reviewer = null, string wallet = Address)
    {
        using (var registered = await merchant.PostAsJsonAsync(new Uri("/v1/settlement/wallets", UriKind.Relative), new { network = "ethereum", address = wallet }))
        {
            var created = (await registered.Content.ReadFromJsonAsync<SettlementWalletResponse>())!;
            if (created.Status == "UnderReview")
            {
                using var approved = await reviewer!.PostAsJsonAsync(
                    new Uri($"/v1/review/settlement-wallets/{created.WalletId}/approve", UriKind.Relative), new { reason = "Test wallet." });
                approved.EnsureSuccessStatusCode();
            }
        }

        using var quoteResponse = await merchant.PostAsJsonAsync(
            new Uri("/v1/quotes", UriKind.Relative), new { sourceCurrency = "INR", sourceAmount = "10000.00", destinationAsset = "USDC@ethereum" });
        var quote = (await quoteResponse.Content.ReadFromJsonAsync<QuoteResponse>())!;
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/v1/orders", UriKind.Relative))
        {
            Content = JsonContent.Create(new { quoteId = quote.QuoteId, customerId = SandboxCustomerDirectory.Verified }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var response = await merchant.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<OrderResponse>())!;
    }

    public static async Task<T> PostAsync<T>(HttpClient client, string path, object body)
    {
        using var response = await client.PostAsJsonAsync(new Uri(path, UriKind.Relative), body);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
}
