using Daleel.Web.Data;
using FluentAssertions;
using Xunit;

namespace Daleel.Web.Tests.Data;

public class AnalyticsServiceTests
{
    private static AnalyticsService Make(SqliteTestContext ctx, DateTime now) => new(ctx.Db, () => now);

    [Fact]
    public async Task RecordSearch_ShowsUpInDashboardAndByType()
    {
        using var ctx = new SqliteTestContext();
        var now = new DateTime(2026, 6, 22, 10, 0, 0, DateTimeKind.Utc);
        var svc = Make(ctx, now);

        await svc.RecordSearchAsync(new AnalyticsEvent { UserId = "u1", Query = "best AC", QueryType = "ask", Geo = "jordan" });
        await svc.RecordSearchAsync(new AnalyticsEvent { UserId = "u1", Query = "Zain", QueryType = "brand", Geo = "jordan" });

        var dash = await svc.GetDashboardAsync();
        dash.SearchesToday.Should().Be(2);
        dash.TopQueries.Should().Contain(t => t.Query == "best AC");

        var byType = await svc.SearchesByTypeAsync(now.AddDays(-1));
        byType.Should().Contain(t => t.Type == "ask" && t.Count == 1);
        byType.Should().Contain(t => t.Type == "brand" && t.Count == 1);
    }

    [Fact]
    public async Task ModerationStats_AggregatesFilteredCountsAndCategories()
    {
        using var ctx = new SqliteTestContext();
        var now = new DateTime(2026, 6, 22, 10, 0, 0, DateTimeKind.Utc);
        var svc = Make(ctx, now);

        await svc.RecordSearchAsync(new AnalyticsEvent { UserId = "u1", FilteredCount = 3, FilteredCategories = "alcohol,pork" });
        await svc.RecordSearchAsync(new AnalyticsEvent { UserId = "u1", FilteredCount = 2, FilteredCategories = "alcohol" });
        await svc.RecordSearchAsync(new AnalyticsEvent { UserId = "u1", FilteredCount = 0, FilteredCategories = "" });

        var (total, withFilters, byCategory) = await svc.ModerationStatsAsync(now.AddDays(-1));

        total.Should().Be(5);
        withFilters.Should().Be(2);
        byCategory.Should().Contain(c => c.Category == "alcohol" && c.Count == 2);
        byCategory.Should().Contain(c => c.Category == "pork" && c.Count == 1);
    }

    [Fact]
    public void HashIp_IsStableAndNotReversible()
    {
        var h1 = IAnalyticsService.HashIp("1.2.3.4");
        var h2 = IAnalyticsService.HashIp("1.2.3.4");
        h1.Should().Be(h2);
        h1.Should().NotBe("1.2.3.4");
        IAnalyticsService.HashIp(null).Should().BeNull();
    }
}
