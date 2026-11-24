using System.Diagnostics.CodeAnalysis;
using FiatCryptoGateway.Application.Configuration;
using FiatCryptoGateway.Application.Ports;
using FiatCryptoGateway.Domain.Assets;
using FiatCryptoGateway.Domain.Compliance;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FiatCryptoGateway.Application.Compliance;

/// <summary>
/// Screens one order: the customer's KYC level, sanctions status and country, the destination wallet's
/// risk, and the amount. It fails closed: if any screening provider errors, the order is held for a
/// person to review and is never approved by default.
/// </summary>
public sealed partial class ComplianceScreeningService(
    ICustomerDirectory customers,
    IWalletScreening walletScreening,
    IOptions<ComplianceOptions> options,
    TimeProvider time,
    ILogger<ComplianceScreeningService> logger)
{
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Fail closed: any screening failure must become a manual review, never an approval or a crash mid-payment.")]
    public async Task<ComplianceDecision> ScreenAsync(
        Guid orderId,
        string clientId,
        string customerId,
        Asset destination,
        string destinationAddress,
        Money pivotAmount,
        CancellationToken cancellationToken)
    {
        try
        {
            var customer = await customers.FindAsync(clientId, customerId, cancellationToken);
            if (customer is null)
            {
                return new ComplianceDecision(orderId, ComplianceOutcome.Rejected, ["CUSTOMER_NOT_FOUND"], "gateway", time.GetUtcNow());
            }

            var wallet = await walletScreening.ScreenAsync(destination, destinationAddress, cancellationToken);
            var (outcome, reasons) = ComplianceRules.Evaluate(customer, wallet, pivotAmount, options.Value.ToPolicy());
            return new ComplianceDecision(orderId, outcome, reasons, wallet.Screener, time.GetUtcNow());
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogScreeningFailed(logger, exception, orderId);
            return new ComplianceDecision(orderId, ComplianceOutcome.ManualReview, ["SCREENING_UNAVAILABLE"], "fail-closed", time.GetUtcNow());
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Compliance screening failed for order {OrderId}; holding it for manual review.")]
    private static partial void LogScreeningFailed(ILogger logger, Exception exception, Guid orderId);
}
