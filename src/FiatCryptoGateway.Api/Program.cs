using FiatCryptoGateway.Api;
using FiatCryptoGateway.Api.Checkout;
using FiatCryptoGateway.Api.Review;
using FiatCryptoGateway.Api.Endpoints;

if (args is [HealthProbe.Argument, ..])
{
    return await HealthProbe.CheckAsync();
}

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddGatewayApi(builder.Configuration);

var app = builder.Build();

// First, so the rate limits and logs see the client's address rather than the proxy's.
app.UseForwardedHeaders();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapHealthChecks("/health");
app.MapGatewayEndpoints();
app.MapCheckout();
app.MapReviewConsole();
app.MapReviewerAccess();

app.Run();
return 0;

public partial class Program;
