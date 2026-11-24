using System.Diagnostics.CodeAnalysis;
using FiatCryptoGateway.Application.OnRamp;
using Microsoft.Extensions.Options;

namespace FiatCryptoGateway.Api;

public sealed class ProcessingOptions
{
    public const string SectionName = "Gateway:Processing";

    public bool Enabled { get; set; } = true;

    public int IntervalSeconds { get; set; } = 5;
}

/// <summary>
/// Periodically expires unpaid orders and resumes orders that stopped mid-way (for example because a
/// provider was briefly unavailable). Every step it calls is idempotent, so overlapping with a webhook
/// is harmless.
/// </summary>
public sealed partial class OrderProcessingWorker(
    OnRampService onRamp,
    IOptions<ProcessingOptions> options,
    TimeProvider time,
    ILogger<OrderProcessingWorker> logger) : BackgroundService
{
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "A failed cycle is logged and retried on the next tick; the worker must keep running.")]
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, options.Value.IntervalSeconds)), time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await onRamp.ExpireOverdueOrdersAsync(stoppingToken);
                await onRamp.ProcessPendingOrdersAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogCycleFailed(logger, exception);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "An order-processing cycle failed; retrying on the next tick.")]
    private static partial void LogCycleFailed(ILogger logger, Exception exception);
}
