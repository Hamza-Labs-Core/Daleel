namespace Daleel.Web.Events;

/// <summary>Per-provider rollup for the admin dashboard.</summary>
public sealed record ProviderUsageRow(string Provider, int Count, decimal Cost, double AvgMs, int Errors);

/// <summary>Per-category rollup (search/scrape/llm/…) for the cost breakdown.</summary>
public sealed record CategoryUsageRow(string Category, int Count, decimal Cost);

/// <summary>
/// A time-windowed usage + cost report, aggregated from raw <see cref="PipelineEvent"/> rows.
/// Built by a pure function so the rollup maths is unit-testable without a live event store; the
/// Postgres store simply loads the window's events and hands them here.
/// </summary>
public sealed record UsageReport(
    int TotalEvents,
    decimal TotalCost,
    IReadOnlyList<ProviderUsageRow> Providers,
    IReadOnlyList<CategoryUsageRow> Categories)
{
    public static UsageReport Empty { get; } =
        new(0, 0m, Array.Empty<ProviderUsageRow>(), Array.Empty<CategoryUsageRow>());

    /// <summary>Rolls a flat event list up into per-provider and per-category totals, cost-sorted.</summary>
    public static UsageReport Build(IReadOnlyList<PipelineEvent> events)
    {
        if (events.Count == 0)
        {
            return Empty;
        }

        var providers = events
            .GroupBy(e => e.Provider)
            .Select(g => new ProviderUsageRow(
                g.Key,
                g.Count(),
                g.Sum(e => e.EstimatedCost),
                g.Average(e => e.DurationMs),
                g.Count(e => !e.Success)))
            .OrderByDescending(r => r.Cost)
            .ToList();

        var categories = events
            .GroupBy(e => e.Category)
            .Select(g => new CategoryUsageRow(g.Key, g.Count(), g.Sum(e => e.EstimatedCost)))
            .OrderByDescending(r => r.Cost)
            .ToList();

        return new UsageReport(events.Count, events.Sum(e => e.EstimatedCost), providers, categories);
    }
}
