using Microsoft.EntityFrameworkCore;

namespace Daleel.Web.Events;

/// <summary>
/// The live event store, backed by <see cref="EventStoreDbContext"/> on PostgreSQL. Writes are
/// best-effort and never throw into the caller (a logging failure must not fail a user's search);
/// the usage report loads the window's rows and rolls them up with the pure <see cref="UsageReport"/>
/// aggregator so the maths stays provider-agnostic and unit-tested.
/// </summary>
public sealed class PostgresEventStore : IEventStore
{
    private readonly IDbContextFactory<EventStoreDbContext> _factory;
    private readonly ILogger<PostgresEventStore> _logger;

    public PostgresEventStore(
        IDbContextFactory<EventStoreDbContext> factory, ILogger<PostgresEventStore> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public bool IsEnabled => true;

    public async Task RecordBatchAsync(IReadOnlyCollection<PipelineEvent> events, CancellationToken ct = default)
    {
        if (events.Count == 0)
        {
            return;
        }

        try
        {
            await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
            db.Events.AddRange(events);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Telemetry is non-critical: swallow so a Postgres hiccup never breaks a search.
            _logger.LogWarning(ex, "Failed to persist {Count} pipeline event(s)", events.Count);
        }
    }

    public async Task<UsageReport> GetUsageAsync(DateTimeOffset since, CancellationToken ct = default)
    {
        try
        {
            await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
            // The window is one app's events over today/week/month — small enough to roll up in memory,
            // which lets the aggregation stay a pure, tested function rather than provider SQL.
            var rows = await db.Events.AsNoTracking()
                .Where(e => e.Timestamp >= since)
                .ToListAsync(ct).ConfigureAwait(false);
            return UsageReport.Build(rows);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to load usage report since {Since}", since);
            return UsageReport.Empty;
        }
    }

    public async Task<IReadOnlyList<PipelineEvent>> RecentAsync(int take, CancellationToken ct = default)
    {
        try
        {
            await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
            return await db.Events.AsNoTracking()
                .OrderByDescending(e => e.Timestamp)
                .Take(Math.Clamp(take, 1, 500))
                .ToListAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to load recent events");
            return Array.Empty<PipelineEvent>();
        }
    }

    public async Task<IReadOnlyList<PipelineEvent>> ForSearchAsync(string searchId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(searchId))
        {
            return Array.Empty<PipelineEvent>();
        }

        try
        {
            await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
            // The (SearchId) index keeps this cheap; oldest-first is the natural timeline order.
            return await db.Events.AsNoTracking()
                .Where(e => e.SearchId == searchId)
                .OrderBy(e => e.Timestamp)
                .ToListAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to load events for search {SearchId}", searchId);
            return Array.Empty<PipelineEvent>();
        }
    }

    public async Task<IReadOnlyDictionary<string, SearchEventSummary>> SummarizeBySearchAsync(
        IReadOnlyCollection<string> searchIds, CancellationToken ct = default)
    {
        if (searchIds.Count == 0)
        {
            return new Dictionary<string, SearchEventSummary>();
        }

        try
        {
            await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
            // Pull just this page of searches' events (bounded by the displayed list) and roll them up
            // with the pure grouping function — same shape as GetUsageAsync, kept unit-testable.
            var rows = await db.Events.AsNoTracking()
                .Where(e => e.SearchId != null && searchIds.Contains(e.SearchId))
                .ToListAsync(ct).ConfigureAwait(false);
            return SearchEventSummary.GroupBySearch(rows);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to summarize events for {Count} search(es)", searchIds.Count);
            return new Dictionary<string, SearchEventSummary>();
        }
    }
}
