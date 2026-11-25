using System.Globalization;
using FiatCryptoGateway.Application.OnRamp;
using FiatCryptoGateway.Application.Ports;
using FiatCryptoGateway.Application.Reconciliation;
using FiatCryptoGateway.Domain.Assets;
using FiatCryptoGateway.Domain.Ledger;
using FiatCryptoGateway.Domain.Orders;
using FiatCryptoGateway.Domain.Pricing;

namespace FiatCryptoGateway.Api.Contracts;

// Amounts and rates travel as strings so JSON clients (notably JavaScript) never lose precision.

public sealed record CreateQuoteRequest(string? SourceCurrency, decimal? SourceAmount, string? DestinationAsset);

public sealed record CreateOrderRequest(Guid? QuoteId, string? CustomerId, string? DestinationAddress);

public sealed record SimulatePaymentRequest(decimal? Amount, int? LateByMinutes);

public sealed record SimulateConfirmationsRequest(int? Confirmations);

public sealed record ReviewActionRequest(string? Reason, string? ReportReference);

public sealed record ErrorDetail(string Code, string Message);

public sealed record ErrorResponse(ErrorDetail Error);

public sealed record WebhookAcknowledgement(string Status);

public sealed record EnvironmentResponse(string Mode, bool RealFundsMovement, string FiatProvider, string LiquidityProvider, string Custodian);

public sealed record AssetResponse(string Id, string Kind, string Name, int Scale, string? NetworkFeeAsset)
{
    public static AssetResponse From(Asset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        return new AssetResponse(asset.Id.ToString(), asset.Kind.ToString(), asset.Name, asset.Scale, asset.NetworkFeeAsset?.ToString());
    }
}

public sealed record PriceResponse(string Base, string Quote, string Rate, DateTimeOffset ObservedAt, DateTimeOffset AvailableAt, string Source)
{
    public static PriceResponse From(PriceObservation price)
    {
        ArgumentNullException.ThrowIfNull(price);
        return new PriceResponse(
            price.BaseAsset.ToString(), price.QuoteAsset.ToString(), Format.Rate(price.Rate), price.ObservedAt, price.AvailableAt, price.Source);
    }
}

public sealed record QuoteResponse(
    Guid QuoteId,
    string SourceCurrency,
    string SourceAmount,
    string ServiceFee,
    string NetSourceAmount,
    string DestinationAsset,
    string GrossDestinationAmount,
    string NetworkFee,
    string DestinationAmount,
    string MarketRate,
    string CustomerRate,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<PriceResponse> PricePath)
{
    public static QuoteResponse From(Quote quote)
    {
        ArgumentNullException.ThrowIfNull(quote);
        return new QuoteResponse(
            quote.QuoteId,
            quote.SourceAmount.Asset.Id.ToString(),
            quote.SourceAmount.ToFixedString(),
            quote.ServiceFee.ToFixedString(),
            quote.NetSourceAmount.ToFixedString(),
            quote.DestinationAmount.Asset.Id.ToString(),
            quote.GrossDestinationAmount.ToFixedString(),
            quote.NetworkFee.ToFixedString(),
            quote.DestinationAmount.ToFixedString(),
            Format.Rate(quote.MarketRate),
            Format.Rate(quote.CustomerRate),
            quote.CreatedAt,
            quote.ExpiresAt,
            [.. quote.PricePath.Select(PriceResponse.From)]);
    }
}

public sealed record OrderEventResponse(int Sequence, string? From, string To, DateTimeOffset OccurredAt, string Reason);

public sealed record OrderResponse(
    Guid OrderId,
    string Status,
    Guid QuoteId,
    string CustomerId,
    string DestinationAddress,
    string SourceCurrency,
    string SourceAmount,
    string DestinationAsset,
    string DestinationAmount,
    Uri? CheckoutUrl,
    DateTimeOffset PaymentDeadline,
    string? TransactionHash,
    int Confirmations,
    IReadOnlyList<OrderEventResponse> Events)
{
    /// <summary>
    /// The merchant view hides compliance detail (see <see cref="MerchantView"/>); reviewers get the full view.
    /// </summary>
    public static OrderResponse From(PaymentOrder order, bool fullDetail = false)
    {
        ArgumentNullException.ThrowIfNull(order);
        IEnumerable<OrderEventResponse> events = fullDetail
            ? order.Events.Select(e => new OrderEventResponse(e.Sequence, e.From?.ToString(), e.To.ToString(), e.OccurredAt, e.Reason))
            : MerchantView.Events(order).Select(e => new OrderEventResponse(e.Sequence, e.From, e.To, e.OccurredAt, e.Reason));
        return new OrderResponse(
            order.OrderId,
            fullDetail ? order.Status.ToString() : MerchantView.Status(order.Status),
            order.Quote.QuoteId,
            order.CustomerId,
            order.DestinationAddress,
            order.Quote.SourceAmount.Asset.Id.ToString(),
            order.Quote.SourceAmount.ToFixedString(),
            order.Quote.DestinationAmount.Asset.Id.ToString(),
            order.Quote.DestinationAmount.ToFixedString(),
            order.CheckoutUrl,
            order.PaymentDeadline,
            order.Transfer?.TransactionHash,
            order.Confirmations,
            [.. events]);
    }
}

/// <summary>One merchant notification and how its delivery went (reviewer view).</summary>
public sealed record WebhookDeliveryResponse(
    Guid EventId,
    int OrderEventSequence,
    string Status,
    int Attempts,
    DateTimeOffset? DeliveredAt,
    DateTimeOffset? GaveUpAt,
    string? LastError)
{
    public static WebhookDeliveryResponse From(OutboxMessageStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var state = status.DeliveredAt is not null ? "Delivered" : status.DeadAt is not null ? "GaveUp" : "Pending";
        return new WebhookDeliveryResponse(
            status.MessageId, status.Sequence, state, status.Attempts, status.DeliveredAt, status.DeadAt, status.LastError);
    }
}

public sealed record ReconcileRequest(string? Day);

/// <summary>A difference between a provider's report and the books. Amounts are null for the side that lacks the movement.</summary>
public sealed record DiscrepancyResponse(
    string Type,
    string Kind,
    string Reference,
    Guid? OrderId,
    string? ReportAmount,
    string? ReportAsset,
    string? LedgerAmount,
    string? LedgerAsset)
{
    public static DiscrepancyResponse From(Discrepancy discrepancy)
    {
        ArgumentNullException.ThrowIfNull(discrepancy);
        return new DiscrepancyResponse(
            discrepancy.Code,
            discrepancy.Kind,
            discrepancy.Reference,
            discrepancy.OrderId,
            discrepancy.ReportAmount?.ToFixedString(),
            discrepancy.ReportAmount?.Asset.Id.ToString(),
            discrepancy.LedgerAmount?.ToFixedString(),
            discrepancy.LedgerAmount?.Asset.Id.ToString());
    }
}

public sealed record ReconciliationRunResponse(
    Guid RunId,
    string Provider,
    string Day,
    DateTimeOffset WindowStart,
    DateTimeOffset WindowEnd,
    DateTimeOffset RanAt,
    int ReportLines,
    int LedgerEntries,
    int Matched,
    bool Clean,
    IReadOnlyList<DiscrepancyResponse> Discrepancies)
{
    public static ReconciliationRunResponse From(ReconciliationRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return new ReconciliationRunResponse(
            run.RunId,
            run.Provider,
            run.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            run.WindowStart,
            run.WindowEnd,
            run.RanAt,
            run.ReportLines,
            run.LedgerEntries,
            run.Matched,
            run.Clean,
            [.. run.Discrepancies.Select(DiscrepancyResponse.From)]);
    }
}

public sealed record ComplianceDecisionResponse(string Outcome, IReadOnlyList<string> Reasons, string Screener, DateTimeOffset DecidedAt);

public sealed record ReviewActionResponse(
    int Sequence,
    string Action,
    string Reviewer,
    string Reason,
    string? ReportReference,
    DateTimeOffset OccurredAt);

/// <summary>A held order as the reviewer sees it, including the screening reasons merchants never see.</summary>
public sealed record ReviewQueueItemResponse(
    Guid OrderId,
    string Merchant,
    string CustomerId,
    string Status,
    string SourceCurrency,
    string SourceAmount,
    string PivotAmount,
    DateTimeOffset HeldSince,
    double WaitingHours,
    bool Overdue,
    IReadOnlyList<string> Reasons)
{
    public static ReviewQueueItemResponse From(Application.Compliance.ReviewQueueItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var order = item.Order;
        return new ReviewQueueItemResponse(
            order.OrderId,
            order.ClientId,
            order.CustomerId,
            order.Status.ToString(),
            order.Quote.SourceAmount.Asset.Id.ToString(),
            order.Quote.SourceAmount.ToFixedString(),
            order.Quote.PivotAmount.ToString(),
            item.HeldSince,
            Math.Round(item.Waiting.TotalHours, 1),
            item.Overdue,
            order.Compliance?.Reasons ?? []);
    }
}

public sealed record ReviewOrderResponse(
    OrderResponse Order,
    string Merchant,
    string? ReportReference,
    IReadOnlyList<ComplianceDecisionResponse> ComplianceHistory,
    IReadOnlyList<ReviewActionResponse> ReviewActions)
{
    public static ReviewOrderResponse From(PaymentOrder order)
    {
        ArgumentNullException.ThrowIfNull(order);
        return new ReviewOrderResponse(
            OrderResponse.From(order, fullDetail: true),
            order.ClientId,
            order.ReportReference,
            [.. order.ComplianceHistory.Select(d => new ComplianceDecisionResponse(d.Outcome.ToString(), d.Reasons, d.Screener, d.DecidedAt))],
            [.. order.ReviewActions.Select(a => new ReviewActionResponse(a.Sequence, a.Kind.ToString(), a.Reviewer, a.Reason, a.ReportReference, a.OccurredAt))]);
    }
}

public sealed record LedgerLineResponse(string Account, string Asset, string Side, string Amount);

public sealed record LedgerEntryResponse(Guid EntryId, string Kind, Guid? OrderId, DateTimeOffset PostedAt, IReadOnlyList<LedgerLineResponse> Lines)
{
    public static LedgerEntryResponse From(JournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new LedgerEntryResponse(
            entry.EntryId,
            entry.Kind,
            entry.OrderId,
            entry.PostedAt,
            [.. entry.Lines.Select(line => new LedgerLineResponse(
                line.Account.Code, line.Account.Asset.ToString(), line.Side.ToString(), line.Amount.ToFixedString()))]);
    }
}

public sealed record BalanceResponse(string Account, string Asset, string DebitMinusCredit);

public sealed record LedgerBalancesResponse(bool BalancedPerAsset, IReadOnlyList<BalanceResponse> Balances);

internal static class Format
{
    /// <summary>Invariant culture, no trailing zeros.</summary>
    public static string Rate(decimal value) => value.ToString("0.############################", CultureInfo.InvariantCulture);
}
