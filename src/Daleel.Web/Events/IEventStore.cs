namespace Daleel.Web.Events;

/// <summary>
/// The append-only pipeline event store, backed by PostgreSQL (<see cref="PostgresEventStore"/>).
/// Every pipeline action is recorded here for the admin usage/cost dashboard.
/// </summary>
public interface IEventStore
{
    /// <summary>Whether the store is active; the dashboard shows a "not configured" hint when false.</summary>
    bool IsEnabled { get; }

    /// <summary>Persists a batch of events (best-effort: a write failure must never fail a search).</summary>
    Task RecordBatchAsync(IReadOnlyCollection<PipelineEvent> events, CancellationToken ct = default);

    /// <summary>Aggregates all events since <paramref name="since"/> into a usage + cost report.</summary>
    Task<UsageReport> GetUsageAsync(DateTimeOffset since, CancellationToken ct = default);

    /// <summary>The most recent raw events (newest first), for the dashboard's live tail.</summary>
    Task<IReadOnlyList<PipelineEvent>> RecentAsync(int take, CancellationToken ct = default);

    /// <summary>
    /// Every event recorded for one search run (correlated by <see cref="PipelineEvent.SearchId"/>,
    /// which is the <c>SearchJob.Id</c>), oldest first — the workflow timeline drill-down.
    /// </summary>
    Task<IReadOnlyList<PipelineEvent>> ForSearchAsync(string searchId, CancellationToken ct = default);

    /// <summary>
    /// Rolls the events of the given search ids up into per-search cost/error summaries in a single
    /// query, so the Workflows completed-list cost column avoids an N+1. Ids with no events are absent.
    /// </summary>
    Task<IReadOnlyDictionary<string, SearchEventSummary>> SummarizeBySearchAsync(
        IReadOnlyCollection<string> searchIds, CancellationToken ct = default);
}

/// <summary>The store used when no Postgres connection is configured: silently drops every event.</summary>
public sealed class NullEventStore : IEventStore
{
    public bool IsEnabled => false;

    public Task RecordBatchAsync(IReadOnlyCollection<PipelineEvent> events, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<UsageReport> GetUsageAsync(DateTimeOffset since, CancellationToken ct = default) =>
        Task.FromResult(UsageReport.Empty);

    public Task<IReadOnlyList<PipelineEvent>> RecentAsync(int take, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PipelineEvent>>(Array.Empty<PipelineEvent>());

    public Task<IReadOnlyList<PipelineEvent>> ForSearchAsync(string searchId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PipelineEvent>>(Array.Empty<PipelineEvent>());

    public Task<IReadOnlyDictionary<string, SearchEventSummary>> SummarizeBySearchAsync(
        IReadOnlyCollection<string> searchIds, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<string, SearchEventSummary>>(
            new Dictionary<string, SearchEventSummary>());
}
