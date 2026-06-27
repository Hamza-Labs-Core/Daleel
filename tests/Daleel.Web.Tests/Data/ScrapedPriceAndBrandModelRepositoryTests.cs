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

    [Fact]
    public async Task BrandModel_GetById_LoadsTheOwningBrand()
    {
        using var ctx = new SqliteTestContext();
        var brand = await SeedBrandAsync(ctx);
        var repo = new BrandModelRepository(ctx.Db);
        var saved = await repo.UpsertAsync(new BrandModel
        {
            BrandId = brand.Id, ModelName = "Galaxy S24", LastRefreshed = DateTimeOffset.UtcNow
        });

        var fetched = await repo.GetByIdAsync(saved.Id);

        fetched.Should().NotBeNull();
        fetched!.ModelName.Should().Be("Galaxy S24");
        fetched.Brand.Should().NotBeNull("the owning brand is eager-loaded for the detail page");
        fetched.Brand!.Name.Should().Be("Samsung");
        (await repo.GetByIdAsync(999_999)).Should().BeNull("an unknown id resolves to nothing");
    }

    [Fact]
    public async Task BrandModel_FindByProductKey_MatchesTheNormalizedBrandModelKey()
    {
        using var ctx = new SqliteTestContext();
        var brand = await SeedBrandAsync(ctx);
        var repo = new BrandModelRepository(ctx.Db);
        await repo.UpsertAsync(new BrandModel
        {
            BrandId = brand.Id, ModelName = "Galaxy S24", LastRefreshed = DateTimeOffset.UtcNow
        });

        // The same key ScrapedPrice/ProductProfile store under: normalized "brand model".
        var key = ProductProfile.KeyFor("Samsung", "Galaxy S24", "Galaxy S24");

        var found = await repo.FindByProductKeyAsync(key);

        found.Should().NotBeNull("the model's brand+model normalizes to the shared product key");
        found!.ModelName.Should().Be("Galaxy S24");
        (await repo.FindByProductKeyAsync("nonexistent product")).Should().BeNull();
    }

    [Fact]
    public async Task ScrapedPrice_LatestForStore_KeepsNewestPerProductCaseInsensitively()
    {
        using var ctx = new SqliteTestContext();
        var repo = new ScrapedPriceRepository(ctx.Db);
        var t0 = DateTimeOffset.UtcNow.AddDays(-3);

        await repo.AddRangeAsync(new[]
        {
            new ScrapedPrice { ProductName = "Galaxy S24", ProductKey = "samsung galaxy s24", StoreName = "SmartBuy",
                Price = 900m, Currency = "JOD", Provider = "context.dev", ScrapedAt = t0 },
            // A newer observation of the same product at the same store, store name in different casing.
            new ScrapedPrice { ProductName = "Galaxy S24", ProductKey = "samsung galaxy s24", StoreName = "smartbuy",
                Price = 850m, Currency = "JOD", Provider = "context.dev", ScrapedAt = t0.AddDays(1) },
            new ScrapedPrice { ProductName = "iPhone 15", ProductKey = "apple iphone 15", StoreName = "SmartBuy",
                Price = 1200m, Currency = "JOD", Provider = "context.dev", ScrapedAt = t0.AddDays(2) },
            // A different store entirely — must not leak into SmartBuy's list.
            new ScrapedPrice { ProductName = "Galaxy S24", ProductKey = "samsung galaxy s24", StoreName = "Leaders",
                Price = 880m, Currency = "JOD", Provider = "context.dev", ScrapedAt = t0.AddDays(2) }
        });

        var carried = await repo.LatestForStoreAsync("SmartBuy");

        carried.Should().HaveCount(2, "two distinct products at SmartBuy, collapsed to the current price each");
        carried.Single(p => p.ProductKey == "samsung galaxy s24").Price.Should().Be(850m, "newest wins, casing ignored");
        carried.Single(p => p.ProductKey == "apple iphone 15").Price.Should().Be(1200m);
    }

    [Fact]
    public async Task ScrapedPrice_HistoryForProduct_ReturnsObservationsNewestFirst()
    {
        using var ctx = new SqliteTestContext();
        var repo = new ScrapedPriceRepository(ctx.Db);
        const string key = "samsung galaxy s24";
        var t0 = DateTimeOffset.UtcNow.AddDays(-5);

        await repo.AddRangeAsync(new[]
        {
            new ScrapedPrice { ProductName = "Galaxy S24", ProductKey = key, StoreName = "A", Price = 900m, Provider = "context.dev", ScrapedAt = t0 },
            new ScrapedPrice { ProductName = "Galaxy S24", ProductKey = key, StoreName = "A", Price = 880m, Provider = "context.dev", ScrapedAt = t0.AddDays(2) },
            new ScrapedPrice { ProductName = "Galaxy S24", ProductKey = key, StoreName = "B", Price = 870m, Provider = "context.dev", ScrapedAt = t0.AddDays(4) }
        });

        var history = await repo.HistoryForProductAsync(key);

        history.Should().HaveCount(3, "history is the full append-only series, not collapsed per store");
        history.Select(h => h.Price).Should().ContainInOrder(870m, 880m, 900m);
    }
}
