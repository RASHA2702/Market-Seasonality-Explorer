using FiatCryptoGateway.Api;
using FiatCryptoGateway.Api.Checkout;
using FiatCryptoGateway.Api.Review;
using FiatCryptoGateway.Api.Endpoints;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddGatewayApi(builder.Configuration);

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapHealthChecks("/health");
app.MapGatewayEndpoints();
app.MapCheckout();
app.MapReviewConsole();
app.MapReviewerAccess();

app.Run();

public partial class Program;
