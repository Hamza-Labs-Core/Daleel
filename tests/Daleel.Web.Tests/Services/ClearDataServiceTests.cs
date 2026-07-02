using Daleel.Web.Data;
using Daleel.Web.Services;
using Daleel.Web.Tests.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Daleel.Web.Tests.Services;

/// <summary>
/// The admin "Data Management" bulk wipes behind <see cref="ClearDataService"/>: each clear must remove
/// exactly its own table(s) and report an accurate count, the catalogue wipe must cascade brand-model
/// dependents and leave unrelated rows (e.g. search history, system config) untouched, and a wipe with
/// Elsa persistence unconfigured must report "not configured" (null) rather than crash. Runs against real
/// PostgreSQL so the EF DELETE translation and FK cascades match production.
/// </summary>
public class ClearDataServiceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    // No Elsa management DbContext factory is registered, so ClearWorkflowHistoryAsync resolves null.
    private static IServiceProvider EmptyServices() => new ServiceCollection().BuildServiceProvider();

    private static ClearDataService NewService(DaleelDbContext db) =>
        new(db, EmptyServices(), NullLogger<ClearDataService>.Instance);

    [Fact]
    public async Task ClearSearchCache_RemovesAllCacheEntries_AndReportsCount()
    {
        using var ctx = new PostgresTestContext();
        ctx.Db.SearchCache.AddRange(
            new SearchCache { CacheKey = "a", Layer = "provider", Payload = "{}", CreatedAt = Now, ExpiresAt = Now.AddDays(1) },
            new SearchCache { CacheKey = "b", Layer = "result", Payload = "{}", CreatedAt = Now, ExpiresAt = Now.AddDays(1) },
            new SearchCache { CacheKey = "c", Layer = "result", Payload = "{}", CreatedAt = Now, ExpiresAt = Now.AddDays(1) });
        await ctx.Db.SaveChangesAsync();

        var removed = await NewService(ctx.NewContext()).ClearSearchCacheAsync();

        removed.Should().Be(3);
        await using var verify = ctx.NewContext();
        (await verify.SearchCache.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ClearSearchCache_AlsoWipesRenderedResults_SoNoStaleDataSurvives()
    {
        using var ctx = new PostgresTestContext();

        // The dedup cache...
        ctx.Db.SearchCache.Add(new SearchCache
        {
            CacheKey = "k", Layer = "result", Payload = "{}", CreatedAt = Now, ExpiresAt = Now.AddDays(1)
        });
        // ...and the materialized result surfaces a user actually sees on reload — the bug being fixed.
        ctx.Db.UserConversations.Add(new UserConversation
        {
            UserId = "u1", CurrentStatus = "completed", CurrentResultJson = "{\"old\":true}", CurrentResultType = "ask"
        });
        ctx.Db.SearchJobs.Add(new SearchJob
        {
            UserId = "u1", Query = "old phones", Status = JobStatus.Completed,
            ResultJson = "{\"old\":true}", CreatedAt = Now
        });
        var history = new SearchHistoryEntry
        {
            UserId = "u1", Query = "old phones", QueryType = "ask", Geo = "jordan",
            ResultJson = "{\"old\":true}", CreatedAt = Now
        };
        ctx.Db.SearchHistory.Add(history);
        await ctx.Db.SaveChangesAsync();
        ctx.Db.SavedResults.Add(new SavedResult
        {
            UserId = "u1", SearchHistoryId = history.Id, Title = "old phones",
            ResultJson = "{\"old\":true}", ResultType = "ask", CreatedAt = Now
        });
        await ctx.Db.SaveChangesAsync();

        var removed = await NewService(ctx.NewContext()).ClearSearchCacheAsync();

        removed.Should().Be(5, "1 cache + 1 conversation + 1 job + 1 saved + 1 history");
        await using var verify = ctx.NewContext();
        (await verify.SearchCache.CountAsync()).Should().Be(0);
        (await verify.UserConversations.CountAsync()).Should().Be(0, "the source of truth the UI renders on load must be gone");
        (await verify.SearchJobs.CountAsync()).Should().Be(0);
        (await verify.SearchHistory.CountAsync()).Should().Be(0);
        (await verify.SavedResults.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ClearProducts_RemovesCatalogue_CascadesModels_LeavesUnrelatedRows()
    {
        using var ctx = new PostgresTestContext();

        var brand = new Brand
        {
            Name = "Samsung", NameKey = Brand.Normalize("Samsung"),
            Website = "https://samsung.com", LastRefreshed = Now
        };
        ctx.Db.Brands.Add(brand);
        await ctx.Db.SaveChangesAsync();

        var model = new BrandModel
        {
            BrandId = brand.Id, ModelName = "Galaxy S24", ModelKey = BrandModel.Normalize("Galaxy S24"),
            LocalPrice = 999, Currency = "JOD", SourceUrl = "https://samsung.com/s24",
            LastRefreshed = Now, DiscoveredAt = Now
        };
        ctx.Db.BrandModels.Add(model);
        await ctx.Db.SaveChangesAsync();

        // A vision-match verdict hanging off the model — must vanish via the DB-level cascade.
        ctx.Db.VisionMatchCaches.Add(new VisionMatchCache
        {
            StoreImageHash = "hash-1", BrandModelId = model.Id,
            MatchedModelName = "Galaxy S24", MatchedAt = Now
        });
        ctx.Db.Stores.Add(new Store
        {
            Name = "Smart Buy", NameKey = Store.Normalize("Smart Buy"), Location = "Amman", LastRefreshed = Now
        });
        ctx.Db.ScrapedPrices.Add(new ScrapedPrice
        {
            ProductKey = "galaxy-s24", StoreName = "Smart Buy", Price = 999, Currency = "JOD", ScrapedAt = Now
        });
        ctx.Db.ProductProfiles.Add(new ProductProfile
        {
            Name = "Galaxy S24", NameKey = "galaxy-s24", LastRefreshed = Now
        });
        // Unrelated row that must survive the catalogue wipe.
        ctx.Db.SearchHistory.Add(new SearchHistoryEntry
        {
            UserId = "u1", Query = "phones", CreatedAt = Now
        });
        await ctx.Db.SaveChangesAsync();

        var result = await NewService(ctx.NewContext()).ClearProductsAsync();

        result.Models.Should().Be(1);
        result.Brands.Should().Be(1);
        result.Stores.Should().Be(1);
        result.ScrapedPrices.Should().Be(1);
        result.ProductProfiles.Should().Be(1);
        result.Total.Should().Be(5);

        await using var verify = ctx.NewContext();
        (await verify.BrandModels.CountAsync()).Should().Be(0);
        (await verify.Brands.CountAsync()).Should().Be(0);
        (await verify.Stores.CountAsync()).Should().Be(0);
        (await verify.ScrapedPrices.CountAsync()).Should().Be(0);
        (await verify.ProductProfiles.CountAsync()).Should().Be(0);
        (await verify.VisionMatchCaches.CountAsync()).Should().Be(0, "verdicts cascade when their model is deleted");
        (await verify.SearchHistory.CountAsync()).Should().Be(1, "unrelated tables are untouched");
    }

    [Fact]
    public async Task ClearWorkflowHistory_ReturnsNull_WhenPersistenceNotConfigured()
    {
        using var ctx = new PostgresTestContext();

        var removed = await NewService(ctx.NewContext()).ClearWorkflowHistoryAsync();

        removed.Should().BeNull("no Elsa management DbContext factory is registered");
    }

    [Fact]
    public async Task ClearAll_ClearsCacheAndCatalogue_AndReportsTotals()
    {
        using var ctx = new PostgresTestContext();

        ctx.Db.SearchCache.Add(new SearchCache
        {
            CacheKey = "k", Layer = "result", Payload = "{}", CreatedAt = Now, ExpiresAt = Now.AddDays(1)
        });
        var brand = new Brand { Name = "LG", NameKey = Brand.Normalize("LG"), LastRefreshed = Now };
        ctx.Db.Brands.Add(brand);
        await ctx.Db.SaveChangesAsync();
        ctx.Db.BrandModels.Add(new BrandModel
        {
            BrandId = brand.Id, ModelName = "OLED C4", ModelKey = BrandModel.Normalize("OLED C4"),
            LocalPrice = 1500, Currency = "JOD", SourceUrl = "https://lg.com/c4",
            LastRefreshed = Now, DiscoveredAt = Now
        });
        await ctx.Db.SaveChangesAsync();

        var result = await NewService(ctx.NewContext()).ClearAllAsync();

        result.SearchCache.Should().Be(1);
        result.Products.Models.Should().Be(1);
        result.Products.Brands.Should().Be(1);
        result.WorkflowInstances.Should().BeNull();
        result.Total.Should().Be(3);

        await using var verify = ctx.NewContext();
        (await verify.SearchCache.CountAsync()).Should().Be(0);
        (await verify.BrandModels.CountAsync()).Should().Be(0);
        (await verify.Brands.CountAsync()).Should().Be(0);
    }
}
