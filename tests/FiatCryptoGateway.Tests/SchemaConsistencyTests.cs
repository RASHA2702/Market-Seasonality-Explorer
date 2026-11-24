using System.Text.RegularExpressions;
using FiatCryptoGateway.Domain.Orders;

namespace FiatCryptoGateway.Tests;

/// <summary>
/// The order state machine exists twice: in <see cref="PaymentOrder"/> and in the database migration.
/// These tests fail if the two ever disagree.
/// </summary>
public sealed partial class SchemaConsistencyTests
{
    private static readonly Lazy<string> Migration = new(() =>
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FiatCryptoGateway.sln")))
        {
            directory = directory.Parent;
        }

        var path = Path.Combine(
            directory?.FullName ?? throw new InvalidOperationException("Repository root not found."),
            "db", "migrations", "001_gateway_foundation.sql");
        return File.ReadAllText(path);
    });

    [Fact]
    public void DatabaseStatuses_MatchTheDomainStatuses()
    {
        var database = StatusRows().Select(row => row.Status).ToHashSet(StringComparer.Ordinal);
        var domain = Enum.GetValues<OrderStatus>().Select(ToDatabaseName).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(domain.Order(StringComparer.Ordinal), database.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void DatabaseTransitions_MatchTheDomainTransitions()
    {
        var database = Block("INSERT INTO gateway.order_transition")
            .Let(block => TransitionRow().Matches(block))
            .Select(match => $"{match.Groups[1].Value}->{match.Groups[2].Value}")
            .Order(StringComparer.Ordinal);

        var statuses = Enum.GetValues<OrderStatus>();
        var domain = statuses
            .SelectMany(from => statuses.Where(to => PaymentOrder.IsAllowedTransition(from, to)).Select(to => (from, to)))
            .Select(pair => $"{ToDatabaseName(pair.from)}->{ToDatabaseName(pair.to)}")
            .Order(StringComparer.Ordinal);

        Assert.Equal(domain, database);
    }

    [Fact]
    public void TerminalFlags_MatchStatusesWithNoWayOut()
    {
        var statuses = Enum.GetValues<OrderStatus>();
        var terminalInDomain = statuses
            .Where(from => !statuses.Any(to => PaymentOrder.IsAllowedTransition(from, to)))
            .Select(ToDatabaseName)
            .Order(StringComparer.Ordinal);
        var terminalInDatabase = StatusRows().Where(row => row.IsTerminal).Select(row => row.Status).Order(StringComparer.Ordinal);

        Assert.Equal(terminalInDomain, terminalInDatabase);
    }

    private static IEnumerable<(string Status, bool IsTerminal)> StatusRows() =>
        StatusRow().Matches(Block("INSERT INTO gateway.order_status"))
            .Select(match => (match.Groups[1].Value, match.Groups[2].Value == "TRUE"));

    private static string Block(string start)
    {
        var sql = Migration.Value;
        var from = sql.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"'{start}' not found in the migration.");
        return sql[from..sql.IndexOf(';', from)];
    }

    private static string ToDatabaseName(OrderStatus status) =>
        PascalBoundary().Replace(status.ToString(), "_$1").ToUpperInvariant();

    [GeneratedRegex(@"\('([A-Z_]+)', (TRUE|FALSE)\)")]
    private static partial Regex StatusRow();

    [GeneratedRegex(@"\('([A-Z_]+)', '([A-Z_]+)'\)")]
    private static partial Regex TransitionRow();

    [GeneratedRegex("(?<!^)([A-Z])")]
    private static partial Regex PascalBoundary();
}

internal static class FunctionalExtensions
{
    public static TResult Let<T, TResult>(this T value, Func<T, TResult> map) => map(value);
}
