using System.Diagnostics.CodeAnalysis;
using FiatCryptoGateway.Application.Payments;
using Microsoft.Extensions.Options;

namespace FiatCryptoGateway.Api;

/// <summary>
/// Periodically finishes what a crash or an outage left half done in the payment processor: ends attempts that ran out
/// of time, captures payments the network approved, and records captured payments on their orders. Every step it calls
/// is safe to repeat, so overlapping with a customer's own request, or with another instance, is harmless.
/// </summary>
public sealed partial class PaymentProcessingWorker(
    PaymentProcessor processor,
    IOptions<ProcessingOptions> options,
    TimeProvider time,
    ILogger<PaymentProcessingWorker> logger) : BackgroundService
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
                var result = await processor.SweepAsync(stoppingToken);
                if (result.Total > 0)
                {
                    LogSwept(logger, result.Expired, result.Captured, result.Recorded, result.Lapsed, result.Failed);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogCycleFailed(logger, exception);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Payment sweep: {Expired} attempts ended, {Captured} payments captured, {Recorded} recorded on their orders, {Lapsed} approvals given up, {Failed} failed.")]
    private static partial void LogSwept(ILogger logger, int expired, int captured, int recorded, int lapsed, int failed);

    [LoggerMessage(Level = LogLevel.Error, Message = "A payment sweep failed; retrying on the next tick.")]
    private static partial void LogCycleFailed(ILogger logger, Exception exception);
}
