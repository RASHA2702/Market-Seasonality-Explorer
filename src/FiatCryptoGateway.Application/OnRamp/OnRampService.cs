using System.Diagnostics.CodeAnalysis;
using FiatCryptoGateway.Application.Compliance;
using FiatCryptoGateway.Application.Configuration;
using FiatCryptoGateway.Application.Ports;
using FiatCryptoGateway.Domain;
using FiatCryptoGateway.Domain.Assets;
using FiatCryptoGateway.Domain.Compliance;
using FiatCryptoGateway.Domain.Ledger;
using FiatCryptoGateway.Domain.Orders;
using FiatCryptoGateway.Domain.Pricing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FiatCryptoGateway.Application.OnRamp;

public sealed record CreateQuoteCommand(string SourceCurrency, decimal SourceAmount, string DestinationAsset);

public sealed record CreateOrderCommand(Guid QuoteId, string CustomerId, string DestinationAddress);

/// <summary>Providers the on-ramp flow depends on, grouped to keep the service's constructor readable.</summary>
public sealed record OnRampProviders(
    IPriceFeed Prices,
    IFiatPaymentProvider Fiat,
    ILiquidityProvider Liquidity,
    ICustodyProvider Custody);

/// <summary>Persistence the on-ramp flow depends on.</summary>
public sealed record OnRampStores(IQuoteStore Quotes, IOrderStore Orders, ILedgerStore Ledger);

/// <summary>
/// Fiat-to-crypto orders: quote, order, fiat capture, compliance, purchase, on-chain send, confirmation.
/// Every step re-reads the order under a per-order lock, checks its state, calls an idempotent provider,
/// and records both the state change and the matching ledger entry. Calling <see cref="ProcessAsync"/>
/// again is always safe: it resumes where the order stopped.
/// </summary>
public sealed partial class OnRampService(
    AssetCatalog catalog,
    OnRampProviders providers,
    OnRampStores stores,
    ComplianceScreeningService compliance,
    IOrderLocks locks,
    IOptions<PricingOptions> pricing,
    TimeProvider time,
    ILogger<OnRampService> logger)
{
    private static readonly OrderStatus[] ActionableStatuses =
        [OrderStatus.FiatReceived, OrderStatus.ReadyToSettle, OrderStatus.CryptoPurchased, OrderStatus.RefundPending];

    // Reasons a client may see. Sanctions and wallet findings are reported only as ORDER_NOT_PERMITTED,
    // so the service does not tip off a customer who is under suspicion.
    private static readonly HashSet<string> ShareableRejectionReasons =
        new(["KYC_REQUIRED", "KYC_LIMIT_EXCEEDED", "COUNTRY_NOT_SUPPORTED"], StringComparer.Ordinal);

    public async Task<Quote> CreateQuoteAsync(string clientId, CreateQuoteCommand command, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentNullException.ThrowIfNull(command);

        var source = catalog.Get(AssetId.Parse(command.SourceCurrency));
        var destination = catalog.Get(AssetId.Parse(command.DestinationAsset));
        if (source.Kind != AssetKind.Fiat || destination.Kind != AssetKind.Crypto)
        {
            throw new GatewayRuleException("UNSUPPORTED_DIRECTION", "Quotes convert a fiat currency into a crypto asset.");
        }

        var amount = new Money(command.SourceAmount, source);
        var policy = pricing.Value.ToPolicy(catalog);
        var route = await providers.Prices.GetRouteAsync(source.Id, destination.Id, cancellationToken);
        var pivotRoute = source.Id == policy.PivotCurrency.Id
            ? [PriceObservation.Identity(source.Id, time.GetUtcNow())]
            : await providers.Prices.GetRouteAsync(source.Id, policy.PivotCurrency.Id, cancellationToken);
        var networkFee = await providers.Custody.EstimateCustomerNetworkFeeAsync(destination, cancellationToken);

        var quote = QuoteCalculator.Calculate(
            new QuoteInputs(clientId, amount, destination, route, pivotRoute, networkFee, policy, time.GetUtcNow()));
        await stores.Quotes.AddAsync(quote, cancellationToken);
        return quote;
    }

    public async Task<PaymentOrder> CreateOrderAsync(string clientId, CreateOrderCommand command, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.CustomerId) || command.CustomerId.Length > 128)
        {
            throw new GatewayRuleException("INVALID_REQUEST", "customerId is required (at most 128 characters).");
        }

        var quote = await stores.Quotes.FindAsync(command.QuoteId, cancellationToken);
        if (quote is null || !string.Equals(quote.ClientId, clientId, StringComparison.Ordinal))
        {
            throw new ResourceNotFoundException("QUOTE_NOT_FOUND", "The quote does not exist.");
        }

        var now = time.GetUtcNow();
        if (quote.IsExpiredAt(now))
        {
            throw new GatewayRuleException("QUOTE_EXPIRED", "The quote has expired; request a new one.");
        }

        var destination = quote.DestinationAmount.Asset;
        if (string.IsNullOrWhiteSpace(command.DestinationAddress) || !providers.Custody.IsValidAddress(destination, command.DestinationAddress))
        {
            throw new GatewayRuleException("INVALID_DESTINATION_ADDRESS", $"Not a valid {destination.Id} address.");
        }

        // Screen before taking the customer's money, so a payment is never collected for an order
        // that could not be completed. Screening runs again after payment.
        var orderId = Guid.NewGuid();
        var precheck = await compliance.ScreenAsync(
            orderId, clientId, command.CustomerId, destination, command.DestinationAddress, quote.PivotAmount, cancellationToken);
        if (precheck.Outcome == ComplianceOutcome.Rejected)
        {
            if (precheck.Reasons.Contains("CUSTOMER_NOT_FOUND"))
            {
                throw new ResourceNotFoundException("CUSTOMER_NOT_FOUND", "The customer does not exist.");
            }

            var code = precheck.Reasons.All(ShareableRejectionReasons.Contains) ? precheck.Reasons[0] : "ORDER_NOT_PERMITTED";
            throw new GatewayRuleException(code, "This customer or destination cannot use the service for this order.");
        }

        if (!await stores.Quotes.TryConsumeAsync(quote.QuoteId, orderId, cancellationToken))
        {
            throw new GatewayRuleException("QUOTE_ALREADY_USED", "Each quote can be used for one order only.");
        }

        var policy = pricing.Value.ToPolicy(catalog);
        var order = PaymentOrder.Create(orderId, clientId, command.CustomerId, quote, command.DestinationAddress, now, policy.PaymentWindow);
        order.RecordPrePaymentScreening(precheck);
        var session = await providers.Fiat.CreateSessionAsync(order, cancellationToken);
        order.AttachPaymentSession(session.SessionId, session.CheckoutUrl);
        await stores.Orders.AddAsync(order, cancellationToken);
        return order;
    }

    public async Task<PaymentOrder> GetOrderAsync(string clientId, Guid orderId, CancellationToken cancellationToken)
    {
        var order = await stores.Orders.FindAsync(orderId, cancellationToken);
        return order is not null && string.Equals(order.ClientId, clientId, StringComparison.Ordinal)
            ? order
            : throw new ResourceNotFoundException("ORDER_NOT_FOUND", "The order does not exist.");
    }

    /// <summary>
    /// Records captured fiat reported by the payment provider, then advances the order.
    /// Returns null when no order matches; such money needs a manual refund.
    /// </summary>
    public async Task<PaymentOrder?> RecordFiatPaymentAsync(FiatPaymentConfirmation payment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payment);
        await using (await locks.AcquireAsync(payment.OrderId, cancellationToken))
        {
            var order = await stores.Orders.FindAsync(payment.OrderId, cancellationToken);
            if (order is null)
            {
                LogUnmatchedPayment(logger, payment.Provider, payment.ProviderPaymentId, payment.OrderId);
                return null;
            }

            if (order.Status is not (OrderStatus.AwaitingFiatPayment or OrderStatus.Expired))
            {
                LogUnexpectedPayment(logger, payment.Provider, payment.ProviderPaymentId, order.OrderId, order.Status);
                return order;
            }

            var now = time.GetUtcNow();
            order.RecordFiatPayment(payment, now);
            await stores.Orders.SaveAsync(order, [OnRampPostings.FiatReceived(order, payment, now)], cancellationToken);
        }

        return await ProcessAsync(payment.OrderId, cancellationToken);
    }

    /// <summary>Advances the order as far as it can go without waiting for an outside event.</summary>
    public async Task<PaymentOrder> ProcessAsync(Guid orderId, CancellationToken cancellationToken)
    {
        await using (await locks.AcquireAsync(orderId, cancellationToken))
        {
            var order = await stores.Orders.FindAsync(orderId, cancellationToken)
                ?? throw new ResourceNotFoundException("ORDER_NOT_FOUND", "The order does not exist.");

            while (await TryAdvanceAsync(order, cancellationToken) is { } journalEntries)
            {
                await stores.Orders.SaveAsync(order, journalEntries, cancellationToken);
            }

            return order;
        }
    }

    public async Task<PaymentOrder> RecordConfirmationsAsync(
        string clientId,
        Guid orderId,
        string transactionHash,
        int confirmations,
        CancellationToken cancellationToken)
    {
        await using (await locks.AcquireAsync(orderId, cancellationToken))
        {
            var order = await GetOrderAsync(clientId, orderId, cancellationToken);
            var required = providers.Custody.RequiredConfirmations(order.Quote.DestinationAmount.Asset);
            order.RecordConfirmations(transactionHash, confirmations, required, time.GetUtcNow());
            await stores.Orders.SaveAsync(order, [], cancellationToken);
            return order;
        }
    }

    /// <summary>A person's decision on an order held for compliance review.</summary>
    public async Task<PaymentOrder> RecordReviewDecisionAsync(
        string clientId,
        Guid orderId,
        bool approve,
        string reviewer,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reviewer);
        await using (await locks.AcquireAsync(orderId, cancellationToken))
        {
            var order = await GetOrderAsync(clientId, orderId, cancellationToken);
            if (order.Status != OrderStatus.ComplianceReview)
            {
                throw new GatewayRuleException("ORDER_STATE_CONFLICT", "Only an order held for review can be reviewed.");
            }

            var now = time.GetUtcNow();
            var decision = new ComplianceDecision(
                orderId,
                approve ? ComplianceOutcome.Approved : ComplianceOutcome.Rejected,
                [approve ? "REVIEWER_APPROVED" : "REVIEWER_REJECTED"],
                $"reviewer:{reviewer}",
                now);
            order.ApplyCompliance(decision, now);
            await stores.Orders.SaveAsync(order, [], cancellationToken);
        }

        return await ProcessAsync(orderId, cancellationToken);
    }

    public async Task<int> ExpireOverdueOrdersAsync(CancellationToken cancellationToken)
    {
        var expired = 0;
        foreach (var orderId in await stores.Orders.FindByStatusAsync([OrderStatus.AwaitingFiatPayment], cancellationToken))
        {
            await using (await locks.AcquireAsync(orderId, cancellationToken))
            {
                var order = await stores.Orders.FindAsync(orderId, cancellationToken);
                if (order is not null && order.ExpireIfOverdue(time.GetUtcNow()))
                {
                    await stores.Orders.SaveAsync(order, [], cancellationToken);
                    expired++;
                }
            }
        }

        return expired;
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "One failing order must not stop the worker from advancing the others; the order stays where it was and is retried.")]
    public async Task<int> ProcessPendingOrdersAsync(CancellationToken cancellationToken)
    {
        var orderIds = await stores.Orders.FindByStatusAsync(ActionableStatuses, cancellationToken);
        foreach (var orderId in orderIds)
        {
            try
            {
                await ProcessAsync(orderId, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogProcessingFailed(logger, exception, orderId);
            }
        }

        return orderIds.Count;
    }

    /// <summary>
    /// Performs the next step, if one can run without an outside event, and returns the journal entries
    /// it produced (possibly none). Returns null when the order must wait.
    /// </summary>
    private async Task<IReadOnlyList<JournalEntry>?> TryAdvanceAsync(PaymentOrder order, CancellationToken cancellationToken)
    {
        switch (order.Status)
        {
            case OrderStatus.FiatReceived:
            {
                var decision = await compliance.ScreenAsync(
                    order.OrderId,
                    order.ClientId,
                    order.CustomerId,
                    order.Quote.DestinationAmount.Asset,
                    order.DestinationAddress,
                    order.Quote.PivotAmount,
                    cancellationToken);
                order.ApplyCompliance(decision, time.GetUtcNow());
                return [];
            }

            case OrderStatus.ReadyToSettle:
            {
                var execution = await providers.Liquidity.BuyAsync(order, cancellationToken);
                var now = time.GetUtcNow();
                order.RecordPurchase(execution, now);
                return [OnRampPostings.CryptoPurchased(order, execution, providers.Custody.Name, now)];
            }

            case OrderStatus.CryptoPurchased:
            {
                var transfer = await providers.Custody.SendAsync(order, cancellationToken);
                var now = time.GetUtcNow();
                order.RecordTransfer(transfer, now);
                return [OnRampPostings.CryptoSent(order, transfer, providers.Custody.Name, now)];
            }

            case OrderStatus.RefundPending:
            {
                var paid = order.FiatPayment ?? throw new InvalidOperationException("A refund needs a recorded payment.");
                var refund = await providers.Fiat.RefundAsync(order, paid.Amount, cancellationToken);
                var now = time.GetUtcNow();
                order.RecordRefund(refund, now);
                return [OnRampPostings.Refunded(order, refund, now)];
            }

            default:
                return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Payment {ProviderPaymentId} from {Provider} matches no order ({OrderId}); it needs a manual refund.")]
    private static partial void LogUnmatchedPayment(ILogger logger, string provider, string providerPaymentId, Guid orderId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Payment {ProviderPaymentId} from {Provider} arrived for order {OrderId} in state {Status}; it needs a manual refund.")]
    private static partial void LogUnexpectedPayment(ILogger logger, string provider, string providerPaymentId, Guid orderId, OrderStatus status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Processing order {OrderId} failed; it will be retried.")]
    private static partial void LogProcessingFailed(ILogger logger, Exception exception, Guid orderId);
}
