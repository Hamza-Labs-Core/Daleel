using Daleel.Web.Data;
using FluentAssertions;
using Xunit;

namespace Daleel.Web.Tests.Data;

/// <summary>
/// Persistence behaviour for the saved <see cref="Brand"/> and <see cref="Store"/> profiles:
/// case-insensitive upsert (so the same brand researched twice updates rather than duplicates),
/// round-tripping of the string-list columns (pros/cons/models/brands), and the staleness query
/// that backs the periodic refresh. The staleness test in particular exercises the Unix-ms value
/// conversion — SQLite cannot compare <see cref="DateTimeOffset"/> in a WHERE clause.
/// </summary>
public class BrandStoreRepositoryTests
{
    [Fact]
    public async Task Brand_Upsert_RoundTripsAllFieldsIncludingLists()
    {
        using var ctx = new SqliteTestContext();
        var repo = new BrandRepository(ctx.Db);

        await repo.UpsertAsync(new Brand
        {
            Name = "Samsung",
            CountryOfOrigin = "South Korea",
            ReputationScore = 8.5,
            Description = "Global electronics maker.",
            Pros = new List<string> { "reliable", "wide range" },
            Cons = new List<string> { "pricey at the top end" },
            PopularModels = new List<string> { "Galaxy S24", "QLED TV" },
            PriceRange = "mid-to-premium",
            Website = "https://samsung.com",
            LastRefreshed = DateTimeOffset.UtcNow
        });

        var fetched = await repo.GetByNameAsync("samsung");

        fetched.Should().NotBeNull();
        fetched!.CountryOfOrigin.Should().Be("South Korea");
        fetched.ReputationScore.Should().Be(8.5);
        fetched.Pros.Should().BeEquivalentTo("reliable", "wide range");
        fetched.Cons.Should().ContainSingle();
        fetched.PopularModels.Should().BeEquivalentTo("Galaxy S24", "QLED TV");
    }

    [Fact]
    public async Task Brand_Upsert_IsCaseInsensitiveAndDoesNotDuplicate()
    {
        using var ctx = new SqliteTestContext();
        var repo = new BrandRepository(ctx.Db);

        await repo.UpsertAsync(new Brand { Name = "LG", Description = "first", LastRefreshed = DateTimeOffset.UtcNow });
        await repo.UpsertAsync(new Brand { Name = "  lg ", Description = "second", LastRefreshed = DateTimeOffset.UtcNow });

        (await repo.CountAsync()).Should().Be(1, "the same brand name (any casing/whitespace) updates in place");
        (await repo.GetByNameAsync("LG"))!.Description.Should().Be("second", "the later refresh wins");
    }

    [Fact]
    public async Task Brand_ListStale_ReturnsOnlyProfilesOlderThanCutoff()
    {
        using var ctx = new SqliteTestContext();
        var repo = new BrandRepository(ctx.Db);
        var now = DateTimeOffset.UtcNow;

        await repo.UpsertAsync(new Brand { Name = "Fresh", LastRefreshed = now.AddDays(-1) });
        await repo.UpsertAsync(new Brand { Name = "Stale", LastRefreshed = now.AddDays(-40) });

        var stale = await repo.ListStaleAsync(olderThan: now.AddDays(-30), max: 10);

        stale.Should().ContainSingle();
        stale.Single().Name.Should().Be("Stale");
    }

    [Fact]
    public async Task Store_Upsert_RoundTripsBrandsCarried()
    {
        using var ctx = new SqliteTestContext();
        var repo = new StoreRepository(ctx.Db);

        await repo.UpsertAsync(new Store
        {
            Name = "Smart Buy",
            Location = "Amman, Jordan",
            Type = "electronics retailer",
            Website = "https://smartbuy-me.com",
            BrandsCarried = new List<string> { "Samsung", "LG", "Sony" },
            Rating = 4.3,
            LastRefreshed = DateTimeOffset.UtcNow
        });

        var fetched = await repo.GetByNameAsync("smart buy");

        fetched.Should().NotBeNull();
        fetched!.Location.Should().Be("Amman, Jordan");
        fetched.BrandsCarried.Should().BeEquivalentTo("Samsung", "LG", "Sony");
        fetched.Rating.Should().Be(4.3);
    }

    [Fact]
    public async Task Brand_IsStale_UsesRefreshAgeAgainstTtl()
    {
        var now = DateTimeOffset.UtcNow;
        var brand = new Brand { Name = "X", LastRefreshed = now.AddDays(-31) };

        brand.IsStale(now, TimeSpan.FromDays(30)).Should().BeTrue();
        brand.IsStale(now, TimeSpan.FromDays(60)).Should().BeFalse();
    }
}
