using System.Diagnostics.CodeAnalysis;
using FiatCryptoGateway.Application.Reconciliation;
using Microsoft.Extensions.Options;

namespace FiatCryptoGateway.Api;

/// <summary>
/// Checks at startup and then every <see cref="ReconciliationOptions.CheckIntervalMinutes"/> whether a day is due
/// for reconciliation. Each provider's day runs once per day on any number of instances.
/// </summary>
public sealed partial class ReconciliationWorker(
    ReconciliationService reconciliation,
    IOptions<ReconciliationOptions> options,
    TimeProvider time,
    ILogger<ReconciliationWorker> logger) : BackgroundService
{
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "A failed check is logged and retried on the next tick; the worker must keep running.")]
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(settings.CheckIntervalMinutes), time);
        do
        {
            try
            {
                await reconciliation.RunDueAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogCheckFailed(logger, exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "A reconciliation check failed; retrying on the next tick.")]
    private static partial void LogCheckFailed(ILogger logger, Exception exception);
}
