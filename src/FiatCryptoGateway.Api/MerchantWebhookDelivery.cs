using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using FiatCryptoGateway.Api.Auth;
using FiatCryptoGateway.Application.Ports;
using FiatCryptoGateway.Application.Webhooks;
using Microsoft.Extensions.Options;

namespace FiatCryptoGateway.Api;

/// <summary>Each merchant's webhook URL and signing secret, from its API client settings.</summary>
public sealed class ConfiguredWebhookTargets(IOptions<ApiKeyOptions> keys) : IMerchantWebhookTargets
{
    public MerchantWebhookTarget? Find(string clientId)
    {
        var client = keys.Value.Clients.FirstOrDefault(candidate => string.Equals(candidate.ClientId, clientId, StringComparison.Ordinal));
        return client is { WebhookUrl: { } url, WebhookSecret: { Length: > 0 } secret } ? new MerchantWebhookTarget(url, secret) : null;
    }
}

/// <summary>
/// Posts a signed notification. Redirects are not followed (a redirect counts as a failure), cookies are not
/// kept, and the client's request logging is removed so merchant URLs stay out of the logs.
/// </summary>
public sealed class HttpWebhookSender(IHttpClientFactory clients) : IWebhookSender
{
    public const string ClientName = "merchant-webhooks";
    public const string SignatureHeader = "X-Gateway-Signature";
    public const string EventIdHeader = "X-Gateway-Event-Id";

    public async Task<WebhookDeliveryResult> SendAsync(Uri url, Guid eventId, string body, string signature, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(SignatureHeader, signature);
        request.Headers.Add(EventIdHeader, eventId.ToString());
        try
        {
            using var response = await clients.CreateClient(ClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return response.IsSuccessStatusCode
                ? WebhookDeliveryResult.Success
                : WebhookDeliveryResult.Failed($"HTTP_{(int)response.StatusCode}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return WebhookDeliveryResult.Failed("TIMEOUT");
        }
        catch (HttpRequestException exception)
        {
            return WebhookDeliveryResult.Failed($"CONNECTION_FAILED:{exception.HttpRequestError}");
        }
    }
}

/// <summary>Sends due merchant notifications every few seconds, draining full batches before it waits.</summary>
public sealed partial class MerchantWebhookWorker(
    MerchantWebhookDispatcher dispatcher,
    IOptions<WebhookOptions> options,
    TimeProvider time,
    ILogger<MerchantWebhookWorker> logger) : BackgroundService
{
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "A failed cycle is logged and retried on the next tick; the worker must keep running.")]
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(settings.IntervalSeconds), time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                while (await dispatcher.DispatchDueAsync(stoppingToken) >= settings.BatchSize)
                {
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogCycleFailed(logger, exception);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "A webhook delivery cycle failed; retrying on the next tick.")]
    private static partial void LogCycleFailed(ILogger logger, Exception exception);
}

public static class MerchantWebhookDeliveryExtensions
{
    public static IServiceCollection AddMerchantWebhookDelivery(this IServiceCollection services)
    {
        services.AddSingleton<IMerchantWebhookTargets, ConfiguredWebhookTargets>();
        services.AddSingleton<IWebhookSender, HttpWebhookSender>();
        services.AddHttpClient(HttpWebhookSender.ClientName, (provider, client) =>
                client.Timeout = TimeSpan.FromSeconds(provider.GetRequiredService<IOptions<WebhookOptions>>().Value.SendTimeoutSeconds))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                AutomaticDecompression = DecompressionMethods.None,
            })
            .RemoveAllLoggers();
        services.AddHostedService<MerchantWebhookWorker>();
        return services;
    }
}
