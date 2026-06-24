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
    Task<IReadOnlyList<ApiCallLog>> ListByJobAsync(int jobId, CancellationToken ct = default);

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

    public async Task<IReadOnlyList<ApiCallLog>> ListByJobAsync(int jobId, CancellationToken ct = default) =>
        await _db.ApiCallLogs.AsNoTracking().Where(c => c.JobId == jobId).OrderBy(c => c.Id).ToListAsync(ct);

    public async Task<(int Calls, decimal Cost)> UserUsageSinceAsync(string userId, DateTimeOffset since, CancellationToken ct = default)
    {
        var rows = _db.ApiCallLogs.AsNoTracking().Where(c => c.UserId == userId && c.CreatedAt >= since);
        var calls = await rows.CountAsync(ct);
        var cost = calls == 0 ? 0m : await rows.SumAsync(c => c.EstimatedCost, ct);
        return (calls, cost);
    }

    public async Task<IReadOnlyList<QueryCost>> RecentJobUsageAsync(string userId, int take, CancellationToken ct = default)
    {
        var perJob = await _db.ApiCallLogs.AsNoTracking()
            .Where(c => c.UserId == userId && c.JobId != null)
            .GroupBy(c => c.JobId!.Value)
            .Select(g => new { JobId = g.Key, Calls = g.Count(), Cost = g.Sum(x => x.EstimatedCost) })
            .OrderByDescending(x => x.JobId) // newest jobs have the highest id
            .Take(take)
            .ToListAsync(ct);

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
            .GroupBy(c => c.Provider)
            .Select(g => new
            {
                Provider = g.Key,
                Calls = g.Count(),
                Cost = g.Sum(x => x.EstimatedCost),
                AvgMs = g.Average(x => (double)x.ResponseTimeMs),
                Errors = g.Count(x => x.Status != "success")
            })
            .OrderByDescending(x => x.Cost)
            .ToListAsync(ct);

        return rows
            .Select(x => new ProviderUsage(x.Provider, x.Calls, x.Cost, x.AvgMs, x.Calls == 0 ? 0 : (double)x.Errors / x.Calls))
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
        var rows = _db.ApiCallLogs.AsNoTracking().Where(c => c.CreatedAt >= since);
        return await rows.AnyAsync(ct) ? await rows.SumAsync(c => c.EstimatedCost, ct) : 0m;
    }

    public async Task<double> AverageCostPerJobAsync(DateTimeOffset since, CancellationToken ct = default)
    {
        var perJob = await _db.ApiCallLogs.AsNoTracking()
            .Where(c => c.CreatedAt >= since && c.JobId != null)
            .GroupBy(c => c.JobId)
            .Select(g => g.Sum(x => x.EstimatedCost))
            .ToListAsync(ct);
        return perJob.Count == 0 ? 0 : (double)perJob.Average();
    }

    public async Task<IReadOnlyList<QueryCost>> MostExpensiveQueriesAsync(DateTimeOffset since, int top, CancellationToken ct = default)
    {
        // Aggregate cost per job, then join to the job's query text.
        var perJob = await _db.ApiCallLogs.AsNoTracking()
            .Where(c => c.CreatedAt >= since && c.JobId != null)
            .GroupBy(c => c.JobId!.Value)
            .Select(g => new { JobId = g.Key, Calls = g.Count(), Cost = g.Sum(x => x.EstimatedCost) })
            .OrderByDescending(x => x.Cost)
            .Take(top)
            .ToListAsync(ct);

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
            .GroupBy(c => c.Model!)
            .Select(g => new TokenUsage(
                g.Key,
                g.Sum(x => (long)(x.InputTokens ?? 0)),
                g.Sum(x => (long)(x.OutputTokens ?? 0)),
                g.Sum(x => x.EstimatedCost)))
            .OrderByDescending(x => x.Cost)
            .ToListAsync(ct);
        return rows;
    }
}
