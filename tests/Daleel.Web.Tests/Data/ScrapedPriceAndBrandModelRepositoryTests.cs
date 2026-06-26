using Daleel.Web.Data;
using FluentAssertions;
using Xunit;

namespace Daleel.Web.Tests.Data;

/// <summary>
/// Persistence behaviour for the two new tables. <see cref="ScrapedPrice"/> is an append-only time series,
/// so the test that matters is "latest price per store" collapsing a history of observations.
/// <see cref="BrandModel"/> is upsert-keyed per brand (re-harvesting updates in place) and carries a real FK
/// to <see cref="Brand"/>. Both exercise the Unix-ms value conversion SQLite needs to order timestamps.
/// </summary>
public class ScrapedPriceAndBrandModelRepositoryTests
{
    private static async Task<Brand> SeedBrandAsync(SqliteTestContext ctx, string name = "Samsung")
    {
        var brand = new Brand { Name = name, LastRefreshed = DateTimeOffset.UtcNow };
        return await new BrandRepository(ctx.Db).UpsertAsync(brand);
    }

    [Fact]
    public async Task ScrapedPrice_LatestForProduct_KeepsNewestObservationPerStore()
    {
        using var ctx = new SqliteTestContext();
        var repo = new ScrapedPriceRepository(ctx.Db);
        var key = ProductProfile.KeyFor("Samsung", "S24", "Galaxy S24");
        var t0 = DateTimeOffset.UtcNow.AddDays(-2);

        await repo.AddRangeAsync(new[]
        {
            new ScrapedPrice { ProductName = "Galaxy S24", ProductKey = key, StoreName = "SmartBuy",
                Price = 900m, Currency = "JOD", Provider = "context.dev", ScrapedAt = t0 },
            new ScrapedPrice { ProductName = "Galaxy S24", ProductKey = key, StoreName = "SmartBuy",
                Price = 850m, Currency = "JOD", Provider = "context.dev", ScrapedAt = t0.AddDays(1) },
            new ScrapedPrice { ProductName = "Galaxy S24", ProductKey = key, StoreName = "Leaders",
                Price = 880m, Currency = "JOD", Provider = "cloudflare-browser", ScrapedAt = t0.AddDays(1) }
        });

        var latest = await repo.LatestForProductAsync(key);

        latest.Should().HaveCount(2, "two distinct stores, collapsed to one current price each");
        latest.Single(p => p.StoreName == "SmartBuy").Price.Should().Be(850m, "the newer observation wins");
        latest.Single(p => p.StoreName == "Leaders").Price.Should().Be(880m);
    }

    [Fact]
    public async Task ScrapedPrice_LatestForProduct_IsScopedToTheProductKey()
    {
        using var ctx = new SqliteTestContext();
        var repo = new ScrapedPriceRepository(ctx.Db);
        await repo.AddAsync(new ScrapedPrice { ProductName = "A", ProductKey = "a", StoreName = "S",
            Price = 1m, Provider = "context.dev", ScrapedAt = DateTimeOffset.UtcNow });
        await repo.AddAsync(new ScrapedPrice { ProductName = "B", ProductKey = "b", StoreName = "S",
            Price = 2m, Provider = "context.dev", ScrapedAt = DateTimeOffset.UtcNow });

        (await repo.LatestForProductAsync("a")).Should().ContainSingle().Which.Price.Should().Be(1m);
    }

    [Fact]
    public async Task BrandModel_Upsert_IsKeyedPerBrandAndDoesNotDuplicate()
    {
        using var ctx = new SqliteTestContext();
        var brand = await SeedBrandAsync(ctx);
        var repo = new BrandModelRepository(ctx.Db);
        var now = DateTimeOffset.UtcNow;

        await repo.UpsertAsync(new BrandModel { BrandId = brand.Id, ModelName = "Galaxy S24",
            LocalPrice = 900m, Currency = "JOD", IsAvailable = true, LastRefreshed = now });
        await repo.UpsertAsync(new BrandModel { BrandId = brand.Id, ModelName = "  galaxy s24 ",
            LocalPrice = 850m, Currency = "JOD", IsAvailable = true, LastRefreshed = now.AddHours(1) });

        (await repo.CountForBrandAsync(brand.Id)).Should().Be(1, "same model (any casing/whitespace) updates in place");
        (await repo.ListByBrandAsync(brand.Id)).Single().LocalPrice.Should().Be(850m, "the later harvest wins");
    }

    [Fact]
    public async Task BrandModel_Upsert_KeepsExistingImageWhenRefreshHasNone()
    {
        using var ctx = new SqliteTestContext();
        var brand = await SeedBrandAsync(ctx);
        var repo = new BrandModelRepository(ctx.Db);
        var now = DateTimeOffset.UtcNow;

        await repo.UpsertAsync(new BrandModel { BrandId = brand.Id, ModelName = "QLED TV",
            ImageUrl = "https://cdn/r2/qled.jpg", IsAvailable = true, LastRefreshed = now });
        await repo.UpsertAsync(new BrandModel { BrandId = brand.Id, ModelName = "QLED TV",
            ImageUrl = null, IsAvailable = false, LastRefreshed = now.AddHours(1) });

        var saved = (await repo.ListByBrandAsync(brand.Id)).Single();
        saved.ImageUrl.Should().Be("https://cdn/r2/qled.jpg", "a refresh without an image must not blank a stored one");
        saved.IsAvailable.Should().BeFalse("other fields still update");
    }

    [Fact]
    public async Task BrandModel_DistinctModelsForOneBrandAllPersist()
    {
        using var ctx = new SqliteTestContext();
        var brand = await SeedBrandAsync(ctx);
        var repo = new BrandModelRepository(ctx.Db);
        var now = DateTimeOffset.UtcNow;

        await repo.UpsertAsync(new BrandModel { BrandId = brand.Id, ModelName = "Galaxy S24", LastRefreshed = now });
        await repo.UpsertAsync(new BrandModel { BrandId = brand.Id, ModelName = "Galaxy A55", LastRefreshed = now });

        (await repo.CountForBrandAsync(brand.Id)).Should().Be(2);
        (await repo.CountAsync()).Should().Be(2);
    }
}
