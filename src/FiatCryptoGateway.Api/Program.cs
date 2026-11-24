using System.Text.Json.Serialization;
using FiatCryptoGateway.Api;
using FiatCryptoGateway.Api.Auth;
using FiatCryptoGateway.Api.Endpoints;
using FiatCryptoGateway.Application;
using FiatCryptoGateway.Sandbox;

var builder = WebApplication.CreateBuilder(args);

// Validates Gateway:Mode at startup: anything other than Sandbox stops the process.
builder.Services.AddGatewayCore(builder.Configuration);
builder.Services.AddSandboxProviders(builder.Configuration);
builder.Services.AddApiKeyAuthentication(builder.Configuration);
builder.Services.AddHealthChecks();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.NumberHandling = JsonNumberHandling.AllowReadingFromString;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.Services.AddOptions<ProcessingOptions>().Bind(builder.Configuration.GetSection(ProcessingOptions.SectionName));
builder.Services.AddHostedService<OrderProcessingWorker>();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();
app.MapHealthChecks("/health");
app.MapGatewayEndpoints();

app.Run();

public partial class Program;
