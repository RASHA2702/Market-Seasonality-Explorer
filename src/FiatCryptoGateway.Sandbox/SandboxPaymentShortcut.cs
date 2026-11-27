using FiatCryptoGateway.Application.Payments;
using FiatCryptoGateway.Domain.Payments;

namespace FiatCryptoGateway.Sandbox;

/// <summary>The sandbox's "pay without typing details" shortcut, kept in one place so every use pays the same way.</summary>
public static class SandboxPaymentShortcut
{
    /// <summary>
    /// Pays an order the way a customer would with the test UPI address that is always approved: starts the payment, then
    /// approves the collect request the UPI network sends. The payment goes through the processor end to end, so what it
    /// records, captures and reports is exactly what a typed payment would. Returns the attempt as it then stands.
    /// </summary>
    public static async Task<PaymentAttemptView> PayWithTestUpiAsync(this PaymentProcessor processor, Guid orderId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(processor);
        var started = await processor.StartAsync(orderId, new UpiInput(SandboxTestInstruments.SuccessfulUpiAddress), cancellationToken);
        return started.Status == PaymentAttemptStatus.AwaitingAuthentication
            ? await processor.AuthenticateAsync(orderId, started.AttemptId, new ApprovalResponse(approved: true), cancellationToken)
            : started;
    }
}
