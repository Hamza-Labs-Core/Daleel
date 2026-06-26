using Daleel.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Daleel.Web.Tests.Data;

/// <summary>
/// Regression tests for the admin Analytics + Filtered-content pages. Both crashed with a SQLite
/// translation error because <see cref="ApiCallLog.CreatedAt"/> / <see cref="FilteredContentLog.CreatedAt"/>
/// were stored as TEXT DateTimeOffsets, which SQLite can't compare (`>= since`) or order. Persisting
/// them as Unix-ms longs fixes it — these tests exercise the exact time-windowed queries the pages run.
/// </summary>
public sealed class AdminLogQueryTests
{
    private static ApiCallLog Call(string provider, decimal cost, DateTimeOffset at, int? jobId = 1,
        string status = "success", string? model = null, int? inTok = null, int? outTok = null) => new()
    {
        Provider = provider, Endpoint = "test", JobId = jobId, Status = status, EstimatedCost = cost,
        ResponseTimeMs = 100, CreatedAt = at, Model = model, InputTokens = inTok, OutputTokens = outTok
    };

    [Fact]
    public async Task ApiCallLog_time_windowed_aggregates_translate_and_aggregate()
    {
        using var ctx = new SqliteTestContext();
        ctx.Db.SearchJobs.Add(new SearchJob { Id = 1, UserId = "u", Query = "air conditioner" });
        ctx.Db.ApiCallLogs.AddRange(
            Call("context.dev", 0.10m, DateTimeOffset.UtcNow.AddDays(-1), model: "gpt", inTok: 100, outTok: 50),
            Call("serpapi", 0.05m, DateTimeOffset.UtcNow.AddHours(-2)),
            Call("serpapi", 0.05m, DateTimeOffset.UtcNow.AddHours(-1), status: "error"),
            Call("context.dev", 99m, DateTimeOffset.UtcNow.AddDays(-90))); // outside the 30d window
        await ctx.Db.SaveChangesAsync();

        var repo = new ApiCallLogRepository(ctx.NewContext());
        var since = DateTimeOffset.UtcNow.AddDays(-30);

        // None of these should throw (the bug), and the out-of-window $99 row must be excluded.
        Assert.Equal(0.20m, await repo.TotalCostAsync(since));

        var providers = await repo.ProviderUsageAsync(since);
        Assert.Equal(2, providers.Count);
        var serp = providers.Single(p => p.Provider == "serpapi");
        Assert.Equal(2, serp.Calls);
        Assert.Equal(0.5, serp.ErrorRate, 3); // 1 of 2 errored

        var tokens = await repo.TokenUsageAsync(since);
        Assert.Equal(100, tokens.Single(t => t.Model == "gpt").InputTokens);

        var expensive = await repo.MostExpensiveQueriesAsync(since, 10);
        Assert.Equal("air conditioner", expensive.Single().Query);

        Assert.NotEmpty(await repo.CostPerDayAsync(14));
        Assert.True(await repo.AverageCostPerJobAsync(since) > 0);
    }

    [Fact]
    public async Task FilteredContentLog_count_since_and_recent_translate()
    {
        using var ctx = new SqliteTestContext();
        ctx.Db.FilteredContentLogs.AddRange(
            new FilteredContentLog { Category = "alcohol", CreatedAt = DateTimeOffset.UtcNow.AddDays(-1) },
            new FilteredContentLog { Category = "gambling", CreatedAt = DateTimeOffset.UtcNow.AddDays(-40) });
        await ctx.Db.SaveChangesAsync();

        var repo = new FilteredContentLogRepository(ctx.NewContext());

        Assert.Equal(1, await repo.CountSinceAsync(DateTimeOffset.UtcNow.AddDays(-30)));
        Assert.Equal(2, (await repo.ListRecentAsync(200)).Count);
    }
}
