using FiatCryptoGateway.Domain.Assets;
using FiatCryptoGateway.Domain.Ledger;

namespace FiatCryptoGateway.Application.Reconciliation;

/// <summary>One movement in a provider's report. <see cref="Kind"/> uses the journal entry kinds (<see cref="EntryKinds"/>).</summary>
public sealed record SettlementLine(string Kind, string Reference, Guid? OrderId, Money Amount, DateTimeOffset OccurredAt);

public sealed record SettlementReport(string Provider, DateOnly Day, IReadOnlyList<SettlementLine> Lines);

public enum DiscrepancyType
{
    /// <summary>The provider reports a movement the books do not have, for example a payment whose webhook never arrived.</summary>
    MissingInLedger,

    /// <summary>The books have a movement the provider does not report.</summary>
    MissingInReport,

    /// <summary>Both have the movement, with different amounts or currencies.</summary>
    AmountMismatch,

    /// <summary>The books record one provider movement more than once.</summary>
    DuplicateInLedger,

    /// <summary>The report lists one movement more than once.</summary>
    DuplicateInReport,
}

/// <summary>A difference between a provider's report and the books, with the amount each side shows (null when absent).</summary>
public sealed record Discrepancy(
    DiscrepancyType Type,
    string Kind,
    string Reference,
    Guid? OrderId,
    Money? ReportAmount,
    Money? LedgerAmount)
{
    /// <summary>The stable code shown in the API, for example <c>MISSING_IN_LEDGER</c>.</summary>
    public string Code => Type switch
    {
        DiscrepancyType.MissingInLedger => "MISSING_IN_LEDGER",
        DiscrepancyType.MissingInReport => "MISSING_IN_REPORT",
        DiscrepancyType.AmountMismatch => "AMOUNT_MISMATCH",
        DiscrepancyType.DuplicateInLedger => "DUPLICATE_IN_LEDGER",
        DiscrepancyType.DuplicateInReport => "DUPLICATE_IN_REPORT",
        _ => throw new ArgumentOutOfRangeException(nameof(Type)),
    };
}

/// <summary>
/// One comparison of a provider's report for a day with the books. <see cref="LedgerEntries"/> counts the
/// provider's entries that happened that day; <see cref="Matched"/> counts report lines found in the books with
/// the same amount.
/// </summary>
public sealed record ReconciliationRun(
    Guid RunId,
    string Provider,
    DateOnly Day,
    DateTimeOffset WindowStart,
    DateTimeOffset WindowEnd,
    DateTimeOffset RanAt,
    int ReportLines,
    int LedgerEntries,
    int Matched,
    IReadOnlyList<Discrepancy> Discrepancies)
{
    public bool Clean => Discrepancies.Count == 0;
}

public sealed record ReconciliationResult(int LedgerEntries, int Matched, IReadOnlyList<Discrepancy> Discrepancies);

/// <summary>
/// Compares a provider's report with the books by (kind, provider ID). A report line matches an entry with the
/// same kind and ID wherever the entry's time falls, so a movement booked just after midnight still matches;
/// an entry is expected in the report only if it happened (by the provider's clock) within the report's day.
/// </summary>
public static class Reconciler
{
    public static ReconciliationResult Compare(
        SettlementReport report,
        IReadOnlyList<JournalEntry> ledger,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(ledger);

        var entries = ledger
            .Where(entry => entry.External is { } external && string.Equals(external.Provider, report.Provider, StringComparison.Ordinal))
            .ToList();
        var booked = entries.ToLookup(entry => (entry.Kind, entry.External!.Id));
        var reported = report.Lines.ToLookup(line => (line.Kind, line.Reference));
        var discrepancies = new List<Discrepancy>();
        var matched = 0;

        foreach (var lines in reported)
        {
            var line = lines.First();
            var bookings = booked[lines.Key].ToList();
            if (lines.Skip(1).Any())
            {
                discrepancies.Add(new(DiscrepancyType.DuplicateInReport, line.Kind, line.Reference, line.OrderId, line.Amount, null));
            }
            else if (bookings.Count == 0)
            {
                discrepancies.Add(new(DiscrepancyType.MissingInLedger, line.Kind, line.Reference, line.OrderId, line.Amount, null));
            }
            else if (bookings.Count > 1)
            {
                discrepancies.Add(new(
                    DiscrepancyType.DuplicateInLedger, line.Kind, line.Reference, bookings[0].OrderId ?? line.OrderId, line.Amount, bookings[0].External!.Amount));
            }
            else if (!SameAmount(line.Amount, bookings[0].External!.Amount))
            {
                discrepancies.Add(new(
                    DiscrepancyType.AmountMismatch, line.Kind, line.Reference, bookings[0].OrderId ?? line.OrderId, line.Amount, bookings[0].External!.Amount));
            }
            else
            {
                matched++;
            }
        }

        foreach (var bookings in booked.Where(group => !reported.Contains(group.Key)))
        {
            var sameDay = bookings.Where(entry => InWindow(entry.External!.OccurredAt, windowStart, windowEnd)).ToList();
            if (sameDay.Count == 0)
            {
                continue;
            }

            var entry = sameDay[0];
            var type = bookings.Skip(1).Any() ? DiscrepancyType.DuplicateInLedger : DiscrepancyType.MissingInReport;
            discrepancies.Add(new(type, entry.Kind, entry.External!.Id, entry.OrderId, null, entry.External.Amount));
        }

        return new ReconciliationResult(
            entries.Count(entry => InWindow(entry.External!.OccurredAt, windowStart, windowEnd)),
            matched,
            [.. discrepancies.OrderBy(d => d.Type).ThenBy(d => d.Kind, StringComparer.Ordinal).ThenBy(d => d.Reference, StringComparer.Ordinal)]);
    }

    // Value equality: 100.00 and 100 are the same amount.
    private static bool SameAmount(Money reported, Money booked) =>
        reported.Asset.Id == booked.Asset.Id && reported.Amount == booked.Amount;

    private static bool InWindow(DateTimeOffset at, DateTimeOffset start, DateTimeOffset end) => at >= start && at < end;
}

/// <summary>Calendar days in the reconciliation time zone (India Standard Time by default).</summary>
public static class BusinessDay
{
    /// <summary>The UTC start (inclusive) and end (exclusive) of <paramref name="day"/> in <paramref name="zone"/>.</summary>
    public static (DateTimeOffset Start, DateTimeOffset End) Window(DateOnly day, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return (StartOf(day, zone), StartOf(day.AddDays(1), zone));
    }

    public static DateOnly Of(DateTimeOffset at, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, zone).DateTime);
    }

    private static DateTimeOffset StartOf(DateOnly day, TimeZoneInfo zone)
    {
        var localMidnight = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(localMidnight, zone.GetUtcOffset(localMidnight)).ToUniversalTime();
    }
}
