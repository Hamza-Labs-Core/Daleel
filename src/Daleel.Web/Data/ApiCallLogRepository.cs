using Microsoft.EntityFrameworkCore;

namespace Daleel.Web.Data;

/// <summary>Per-provider usage/cost/health aggregate over a window.</summary>
public sealed record ProviderUsage(string Provider, int Calls, decimal Cost, double AvgMs, double ErrorRate);

/// <summary>LLM token usage per model.</summary>
public sealed record TokenUsage(string Model, long InputTokens, long OutputTokens, decimal Cost);

/// <summary>A search and what it cost (for "most expensive queries").</summary>
public sealed record QueryCost(int JobId, string Query, int Calls, decimal Cost);

/// <summary>Persists and aggregates <see cref="ApiCallLog"/> rows for usage, cost, and analytics.</summary>
public interface IApiCallLogRepository
{
    Task AddBatchAsync(IEnumerable<ApiCallLog> calls, CancellationToken ct = default);

    /// <summary>
    /// The API-call log for one job, scoped to <paramref name="userId"/> (its owner). Filtering by owner
    /// here — not just by job id — means a caller can never read another user's calls by guessing a job id.
    /// </summary>
    Task<IReadOnlyList<ApiCallLog>> ListByJobAsync(int jobId, string userId, CancellationToken ct = default);

    /// <summary>(call count, total cost) for a user since a date.</summary>
    Task<(int Calls, decimal Cost)> UserUsageSinceAsync(string userId, DateTimeOffset since, CancellationToken ct = default);

    /// <summary>A user's recent searches with their API-call count and cost (newest first).</summary>
    Task<IReadOnlyList<QueryCost>> RecentJobUsageAsync(string userId, int take, CancellationToken ct = default);

    // Admin aggregates.
    Task<IReadOnlyList<ProviderUsage>> ProviderUsageAsync(DateTimeOffset since, CancellationToken ct = default);
    Task<IReadOnlyList<(DateTime Day, decimal Cost)>> CostPerDayAsync(int days, CancellationToken ct = default);
    Task<decimal> TotalCostAsync(DateTimeOffset since, CancellationToken ct = default);
    Task<double> AverageCostPerJobAsync(DateTimeOffset since, CancellationToken ct = default);
    Task<IReadOnlyList<QueryCost>> MostExpensiveQueriesAsync(DateTimeOffset since, int top, CancellationToken ct = default);
    Task<IReadOnlyList<TokenUsage>> TokenUsageAsync(DateTimeOffset since, CancellationToken ct = default);
}

public sealed class ApiCallLogRepository : IApiCallLogRepository
{
    private readonly DaleelDbContext _db;

    public ApiCallLogRepository(DaleelDbContext db) => _db = db;

    public async Task AddBatchAsync(IEnumerable<ApiCallLog> calls, CancellationToken ct = default)
    {
        _db.ApiCallLogs.AddRange(calls);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<ApiCallLog>> ListByJobAsync(
        int jobId, string userId, CancellationToken ct = default)
    {
        // Rows store a hashed user id (privacy); hash the lookup the same way to match the owner's own.
        var key = Anonymizer.HashUserId(userId);
        return await _db.ApiCallLogs.AsNoTracking()
            .Where(c => c.JobId == jobId && c.UserId == key)
            .OrderBy(c => c.Id)
            .ToListAsync(ct);
    }

    // NOTE on aggregation: aggregation is done in memory (the windows are small) so the maths stays a
    // pure, tested function, so every cost rollup below materializes the (time-windowed, index-backed)
    // rows first and aggregates in memory. The WHERE stays server-side — CreatedAt is a Unix-ms long, so
    // `>= since` translates fine.

    public async Task<(int Calls, decimal Cost)> UserUsageSinceAsync(string userId, DateTimeOffset since, CancellationToken ct = default)
    {
        // Rows store a hashed user id (privacy); hash the lookup the same way to find the user's own.
        var key = Anonymizer.HashUserId(userId);
        var costs = await _db.ApiCallLogs.AsNoTracking()
            .Where(c => c.UserId == key && c.CreatedAt >= since)
            .Select(c => c.EstimatedCost)
            .ToListAsync(ct);
        return (costs.Count, costs.Sum());
    }

    public async Task<IReadOnlyList<QueryCost>> RecentJobUsageAsync(string userId, int take, CancellationToken ct = default)
    {
        var key = Anonymizer.HashUserId(userId);
        var rows = await _db.ApiCallLogs.AsNoTracking()
            .Where(c => c.UserId == key && c.JobId != null)
            .Select(c => new { c.JobId, c.EstimatedCost })
            .ToListAsync(ct);
        var perJob = rows
            .GroupBy(c => c.JobId!.Value)
            .Select(g => new { JobId = g.Key, Calls = g.Count(), Cost = g.Sum(x => x.EstimatedCost) })
            .OrderByDescending(x => x.JobId) // newest jobs have the highest id
            .Take(take)
            .ToList();

        var jobIds = perJob.Select(p => p.JobId).ToList();
        var queries = await _db.SearchJobs.AsNoTracking()
            .Where(j => jobIds.Contains(j.Id))
            .ToDictionaryAsync(j => j.Id, j => j.Query, ct);

        return perJob
            .Select(p => new QueryCost(p.JobId, queries.GetValueOrDefault(p.JobId, "(unknown)"), p.Calls, p.Cost))
            .ToList();
    }

    public async Task<IReadOnlyList<ProviderUsage>> ProviderUsageAsync(DateTimeOffset since, CancellationToken ct = default)
    {
        var rows = await _db.ApiCallLogs.AsNoTracking()
            .Where(c => c.CreatedAt >= since)
            .Select(c => new { c.Provider, c.EstimatedCost, c.ResponseTimeMs, c.Status })
            .ToListAsync(ct);

        return rows
            .GroupBy(c => c.Provider)
            .Select(g => new ProviderUsage(
                g.Key,
                g.Count(),
                g.Sum(x => x.EstimatedCost),
                g.Average(x => (double)x.ResponseTimeMs),
                (double)g.Count(x => x.Status != "success") / g.Count()))
            .OrderByDescending(x => x.Cost)
            .ToList();
    }

    public async Task<IReadOnlyList<(DateTime Day, decimal Cost)>> CostPerDayAsync(int days, CancellationToken ct = default)
    {
        var since = DateTimeOffset.UtcNow.Date.AddDays(-days + 1);
        var rows = await _db.ApiCallLogs.AsNoTracking()
            .Where(c => c.CreatedAt >= since)
            .Select(c => new { c.CreatedAt, c.EstimatedCost })
            .ToListAsync(ct);
        var byDay = rows.GroupBy(r => r.CreatedAt.UtcDateTime.Date)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.EstimatedCost));
        return Enumerable.Range(0, days)
            .Select(i => since.Date.AddDays(i))
            .Select(d => (d, byDay.GetValueOrDefault(d, 0m)))
            .ToList();
    }

    public async Task<decimal> TotalCostAsync(DateTimeOffset since, CancellationToken ct = default)
    {
        var costs = await _db.ApiCallLogs.AsNoTracking()
            .Where(c => c.CreatedAt >= since)
            .Select(c => c.EstimatedCost)
            .ToListAsync(ct);
        return costs.Sum();
    }

    public async Task<double> AverageCostPerJobAsync(DateTimeOffset since, CancellationToken ct = default)
    {
        var rows = await _db.ApiCallLogs.AsNoTracking()
            .Where(c => c.CreatedAt >= since && c.JobId != null)
            .Select(c => new { c.JobId, c.EstimatedCost })
            .ToListAsync(ct);
        var perJob = rows.GroupBy(c => c.JobId!.Value).Select(g => g.Sum(x => x.EstimatedCost)).ToList();
        return perJob.Count == 0 ? 0 : (double)perJob.Average();
    }

    public async Task<IReadOnlyList<QueryCost>> MostExpensiveQueriesAsync(DateTimeOffset since, int top, CancellationToken ct = default)
    {
        // Aggregate cost per job (in memory — see the SUM note above), then join to the job's query text.
        var rows = await _db.ApiCallLogs.AsNoTracking()
            .Where(c => c.CreatedAt >= since && c.JobId != null)
            .Select(c => new { c.JobId, c.EstimatedCost })
            .ToListAsync(ct);
        var perJob = rows
            .GroupBy(c => c.JobId!.Value)
            .Select(g => new { JobId = g.Key, Calls = g.Count(), Cost = g.Sum(x => x.EstimatedCost) })
            .OrderByDescending(x => x.Cost)
            .Take(top)
            .ToList();

        var jobIds = perJob.Select(p => p.JobId).ToList();
        var queries = await _db.SearchJobs.AsNoTracking()
            .Where(j => jobIds.Contains(j.Id))
            .ToDictionaryAsync(j => j.Id, j => j.Query, ct);

        return perJob
            .Select(p => new QueryCost(p.JobId, queries.GetValueOrDefault(p.JobId, "(unknown)"), p.Calls, p.Cost))
            .ToList();
    }

    public async Task<IReadOnlyList<TokenUsage>> TokenUsageAsync(DateTimeOffset since, CancellationToken ct = default)
    {
        var rows = await _db.ApiCallLogs.AsNoTracking()
            .Where(c => c.CreatedAt >= since && c.Model != null && (c.InputTokens != null || c.OutputTokens != null))
            .Select(c => new { c.Model, c.InputTokens, c.OutputTokens, c.EstimatedCost })
            .ToListAsync(ct);
        return rows
            .GroupBy(c => c.Model!)
            .Select(g => new TokenUsage(
                g.Key,
                g.Sum(x => (long)(x.InputTokens ?? 0)),
                g.Sum(x => (long)(x.OutputTokens ?? 0)),
                g.Sum(x => x.EstimatedCost)))
            .OrderByDescending(x => x.Cost)
            .ToList();
    }
}
