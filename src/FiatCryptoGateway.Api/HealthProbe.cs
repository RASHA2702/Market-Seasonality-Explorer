using System.Globalization;

namespace FiatCryptoGateway.Api;

/// <summary>
/// <c>dotnet FiatCryptoGateway.Api.dll --health-check</c> asks the API running on this machine for <c>/health</c> and
/// exits 0 if it answers 200. The container image has no shell or curl, so its health check runs this instead.
/// </summary>
public static class HealthProbe
{
    public const string Argument = "--health-check";

    public static async Task<int> CheckAsync()
    {
        var configured = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS")?.Split(',', ';')[0].Trim();
        var port = int.TryParse(configured, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number is > 0 and < 65536
            ? number
            : 8080;
        using var handler = new HttpClientHandler { UseProxy = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
        try
        {
            using var response = await client.GetAsync(new Uri($"http://127.0.0.1:{port}/health"));
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return 1;
        }
    }
}
