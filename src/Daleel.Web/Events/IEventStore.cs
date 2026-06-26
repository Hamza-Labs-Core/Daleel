namespace Daleel.Web.Events;

/// <summary>
/// The append-only pipeline event store. Implemented over PostgreSQL when configured, or a no-op
/// (<see cref="NullEventStore"/>) when no Postgres connection is set — so the app runs SQLite-only
/// out of the box and gains durable event auditing the moment a connection string is provided.
/// </summary>
public interface IEventStore
{
    /// <summary>False for the no-op store; the dashboard shows a "not configured" hint when false.</summary>
    bool IsEnabled { get; }

    /// <summary>Persists a batch of events (best-effort: a write failure must never fail a search).</summary>
    Task RecordBatchAsync(IReadOnlyCollection<PipelineEvent> events, CancellationToken ct = default);

    /// <summary>Aggregates all events since <paramref name="since"/> into a usage + cost report.</summary>
    Task<UsageReport> GetUsageAsync(DateTimeOffset since, CancellationToken ct = default);

    /// <summary>The most recent raw events (newest first), for the dashboard's live tail.</summary>
    Task<IReadOnlyList<PipelineEvent>> RecentAsync(int take, CancellationToken ct = default);
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
}
