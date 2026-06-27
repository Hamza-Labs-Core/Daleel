using Daleel.Web.Data;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace Daleel.Web.Tests.Data;

public class SystemConfigCacheTests
{
    [Fact]
    public async Task Get_IsCached_AndInvalidatedOnSet()
    {
        using var ctx = new PostgresTestContext();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var cfg = new SystemConfigService(ctx.Db, cache);
        await cfg.SeedDefaultsAsync();

        // First read populates the cache; mutating the row directly (bypassing SetAsync) is NOT seen
        // until the cache is invalidated — proving reads are served from cache, not the DB each time.
        (await cfg.GetIntAsync("ratelimit.search_per_hour", -1)).Should().Be(5);

        var row = ctx.Db.SystemConfig.First(c => c.Key == "ratelimit.search_per_hour");
        row.Value = "999";
        await ctx.Db.SaveChangesAsync();
        (await cfg.GetIntAsync("ratelimit.search_per_hour", -1)).Should().Be(5, "the cached snapshot is still live");

        // SetAsync evicts the cache, so the next read reflects the change immediately.
        await cfg.SetAsync("ratelimit.search_per_hour", "20", "int");
        (await cfg.GetIntAsync("ratelimit.search_per_hour", -1)).Should().Be(20);
    }
}
