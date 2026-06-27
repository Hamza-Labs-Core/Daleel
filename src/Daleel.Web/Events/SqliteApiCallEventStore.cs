using Daleel.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Daleel.Web.Events;

/// <summary>
/// The default (always-on) event store used when no Postgres connection is configured. Rather than
/// dropping everything like <see cref="NullEventStore"/> did, it serves the admin usage/cost dashboard
/// from the SQLite <see cref="ApiCallLog"/> table — which every search run already writes to (see the
/// search runners' <c>PersistAsync</c>). That table holds the provider, endpoint, cost, latency and
/// status of every external call, i.e. everything the dashboard's per-provider / per-category rollups
/// need, so the page shows real analytics out of the box instead of a "not configured" notice.
/// </summary>
/// <remarks>
/// Writes are a no-op: the <see cref="ApiCallLog"/> rows are persisted by the search runners through
/// <see cref="IApiCallLogRepository"/>, so re-recording them here would double-count. The richer custom
/// events (cache hit/miss, profile lookups) that the Postgres store captures simply aren't surfaced in
/// the SQLite view — the provider-call audit is the part operators care about for cost.
///
/// A fresh scope (hence a fresh <see cref="DaleelDbContext"/>) is created per query so this singleton
/// never shares the non-thread-safe context across callers — the same isolation
/// <see cref="PostgresEventStore"/> gets from its <c>IDbContextFactory</c>.
/// </remarks>
public sealed class SqliteApiCallEventStore : IEventStore
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<SqliteApiCallEventStore> _logger;

    public SqliteApiCallEventStore(IServiceScopeFactory scopes, ILogger<SqliteApiCallEventStore> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    public bool IsEnabled => true;

    // No-op: the search runners already persist these calls to ApiCallLog via IApiCallLogRepository.
    public Task RecordBatchAsync(IReadOnlyCollection<PipelineEvent> events, CancellationToken ct = default) =>
        Task.CompletedTask;

    public async Task<UsageReport> GetUsageAsync(DateTimeOffset since, CancellationToken ct = default)
    {
        try
        {
            // The window is one app's calls over today/week/month — small enough to materialize and roll
            // up in memory with the shared, unit-tested UsageReport aggregator. (SQLite also can't SUM a
            // decimal in SQL, so in-memory aggregation is required anyway — see ApiCallLogRepository.)
            var rows = await QueryAsync(q => q.Where(c => c.CreatedAt >= since), ct).ConfigureAwait(false);
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
            return await QueryAsync(
                q => q.OrderByDescending(c => c.CreatedAt).Take(Math.Clamp(take, 1, 500)), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to load recent events");
            return Array.Empty<PipelineEvent>();
        }
    }

    public async Task<IReadOnlyList<PipelineEvent>> ForSearchAsync(string searchId, CancellationToken ct = default)
    {
        // PipelineEvent.SearchId mirrors the SearchJob id (ApiCallLog.JobId), stringified by the runner.
        if (!int.TryParse(searchId, out var jobId))
        {
            return Array.Empty<PipelineEvent>();
        }

        try
        {
            return await QueryAsync(
                q => q.Where(c => c.JobId == jobId).OrderBy(c => c.CreatedAt), ct).ConfigureAwait(false);
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
        var jobIds = searchIds
            .Select(s => int.TryParse(s, out var id) ? id : (int?)null)
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .ToHashSet();
        if (jobIds.Count == 0)
        {
            return new Dictionary<string, SearchEventSummary>();
        }

        try
        {
            var rows = await QueryAsync(
                q => q.Where(c => c.JobId != null && jobIds.Contains(c.JobId.Value)), ct).ConfigureAwait(false);
            return SearchEventSummary.GroupBySearch(rows);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to summarize events for {Count} search(es)", searchIds.Count);
            return new Dictionary<string, SearchEventSummary>();
        }
    }

    // Runs the shaping query against a fresh DaleelDbContext and projects ApiCallLog → PipelineEvent.
    private async Task<List<PipelineEvent>> QueryAsync(
        Func<IQueryable<ApiCallLog>, IQueryable<ApiCallLog>> shape, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DaleelDbContext>();
        var rows = await shape(db.ApiCallLogs.AsNoTracking()).ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(ToEvent).ToList();
    }

    // Maps a logged API call onto the dashboard's event shape, reusing the same category inference the
    // Postgres store uses so the two backends bucket providers identically.
    private static PipelineEvent ToEvent(ApiCallLog c) => new()
    {
        Timestamp = c.CreatedAt,
        Category = PipelineEventFactory.CategoryOf(c.Provider, c.Endpoint),
        EventType = c.Endpoint,
        Provider = c.Provider,
        SearchId = c.JobId?.ToString(),
        DurationMs = c.ResponseTimeMs,
        EstimatedCost = c.EstimatedCost,
        Success = string.Equals(c.Status, "success", StringComparison.OrdinalIgnoreCase),
        MetadataJson = "{}"
    };
}
