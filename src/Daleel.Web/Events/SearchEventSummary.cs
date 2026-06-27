namespace Daleel.Web.Events;

/// <summary>
/// Per-search rollup of pipeline events: how many actions a single search run recorded, their total
/// USD cost, how many failed, and the wall-clock provider time. Drives the cost/status columns of the
/// admin Workflows page's completed list. Built by a pure function (<see cref="GroupBySearch"/>) so the
/// maths is unit-tested without a live event store, mirroring <see cref="UsageReport"/>.
/// </summary>
public sealed record SearchEventSummary(int Count, decimal Cost, int Errors, long TotalMs)
{
    public static SearchEventSummary Empty { get; } = new(0, 0m, 0, 0);

    /// <summary>
    /// Groups a flat event list by <see cref="PipelineEvent.SearchId"/> into per-search summaries.
    /// Events with a null/empty search id are skipped (they're ad-hoc actions, not part of a run).
    /// </summary>
    public static IReadOnlyDictionary<string, SearchEventSummary> GroupBySearch(IEnumerable<PipelineEvent> events) =>
        events
            .Where(e => !string.IsNullOrEmpty(e.SearchId))
            .GroupBy(e => e.SearchId!)
            .ToDictionary(
                g => g.Key,
                g => new SearchEventSummary(
                    g.Count(),
                    g.Sum(e => e.EstimatedCost),
                    g.Count(e => !e.Success),
                    g.Sum(e => e.DurationMs)));
}
