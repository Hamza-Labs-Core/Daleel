using Daleel.Web.Data;
using Daleel.Web.Services;
using Daleel.Web.Tests.Data;
using FluentAssertions;
using Xunit;

namespace Daleel.Web.Tests.Services;

/// <summary>
/// The product detail page reads from saved data, never a live scrape. These tests pin the assembly
/// rules: prices/profile alone are enough to render a page, a harvested <see cref="BrandModel"/> adds
/// specs + image + brand reputation, and a product with nothing saved resolves to null (the page then
/// shows "not yet available"). Real Postgres-backed repositories exercise the same SQL production runs.
/// </summary>
public class ProductDetailDbServiceTests
{
    private static ProductDetailDbService Service(PostgresTestContext ctx) =>
        new(
            new BrandModelRepository(ctx.Db),
            new ProductProfileRepository(ctx.Db),
            new ScrapedPriceRepository(ctx.Db),
            new BrandRepository(ctx.Db));

    [Fact]
    public async Task GetAsync_ReturnsNull_WhenNothingAboutTheProductIsSaved()
    {
        using var ctx = new PostgresTestContext();
        var view = await Service(ctx).GetAsync("p_deadbeef", "Totally Unknown Phone", "jordan");
        view.Should().BeNull("with no catalogue row, profile or scraped price there's nothing to show");
    }

    [Fact]
    public async Task GetAsync_AssemblesFromProfileAndScrapedPrices_WithoutACatalogueRow()
    {
        using var ctx = new PostgresTestContext();
        var profiles = new ProductProfileRepository(ctx.Db);
        var prices = new ScrapedPriceRepository(ctx.Db);
        var now = DateTimeOffset.UtcNow;
        var key = ProductProfile.KeyFor("Samsung", "Galaxy S24", "Galaxy S24");

        await profiles.UpsertAsync(new ProductProfile
        {
            Name = "Samsung Galaxy S24", Brand = "Samsung", Model = "Galaxy S24",
            Details = "A flagship Android phone.", LastRefreshed = now
        });
        await prices.AddRangeAsync(new[]
        {
            new ScrapedPrice { ProductName = "Galaxy S24", ProductKey = key, StoreName = "SmartBuy",
                Price = 900m, Currency = "JOD", SourceUrl = "https://smartbuy/s24", Provider = "context.dev", ScrapedAt = now.AddDays(-2) },
            new ScrapedPrice { ProductName = "Galaxy S24", ProductKey = key, StoreName = "Leaders",
                Price = 850m, Currency = "JOD", SourceUrl = "https://leaders/s24", Provider = "context.dev", ScrapedAt = now }
        });

        // Routed by a stable-id hash (not numeric), so the name reconstructs the lookup key.
        var view = await Service(ctx).GetAsync("p_hash", "Samsung Galaxy S24", "jordan");

        view.Should().NotBeNull();
        view!.Description.Should().Be("A flagship Android phone.");
        view.HasCatalogueProfile.Should().BeFalse("no harvested BrandModel backs this product");
        view.ImageUrl.Should().BeNull();
        view.Specs.Should().BeEmpty("specs come from a catalogue row, which is absent here");
        view.SellerCount.Should().Be(2);
        view.Offers[0].Price.Should().Be(850m, "offers are cheapest-first");
        view.LowestPrice.Should().Be(850m);
        view.Offers[0].StoreId.Should().StartWith("s_", "each store links by its stable id");
    }

    [Fact]
    public async Task GetAsync_ByCatalogueId_IncludesSpecsImageAndBrandReputation()
    {
        using var ctx = new PostgresTestContext();
        var brands = new BrandRepository(ctx.Db);
        var models = new BrandModelRepository(ctx.Db);
        var prices = new ScrapedPriceRepository(ctx.Db);
        var now = DateTimeOffset.UtcNow;

        var brand = await brands.UpsertAsync(new Brand { Name = "Samsung", ReputationScore = 8.5, LastRefreshed = now });
        var model = await models.UpsertAsync(new BrandModel
        {
            BrandId = brand.Id, ModelName = "Galaxy S24",
            SpecsJson = """{ "ram": "8 GB", "storage": "256 GB", "description": "Compact flagship." }""",
            ImageUrl = "https://cdn/r2/s24.jpg", LocalPrice = 950m, Currency = "JOD", LastRefreshed = now
        });
        var key = ProductProfile.KeyFor("Samsung", "Galaxy S24", "Galaxy S24");
        await prices.AddAsync(new ScrapedPrice { ProductName = "Galaxy S24", ProductKey = key, StoreName = "SmartBuy",
            Price = 900m, Currency = "JOD", Provider = "context.dev", ScrapedAt = now });

        // A numeric route id resolves the catalogue row directly.
        var view = await Service(ctx).GetAsync(model.Id.ToString(), "ignored when id is numeric", "jordan");

        view.Should().NotBeNull();
        view!.HasCatalogueProfile.Should().BeTrue();
        view.ImageUrl.Should().Be("https://cdn/r2/s24.jpg");
        view.Specs.Should().ContainKey("ram").WhoseValue.Should().Be("8 GB");
        view.Specs.Should().NotContainKey("description", "a prose description is surfaced separately, not as a spec row");
        view.Description.Should().Be("Compact flagship.");
        view.BrandReputationScore.Should().Be(8.5);
        view.BrandStableId.Should().StartWith("b_");
        view.LowestPrice.Should().Be(900m, "a live scraped price beats the catalogue list price");
    }

    [Fact]
    public async Task GetComparable_ReturnsNull_WhenNeitherSpecsNorPricesAreSaved()
    {
        using var ctx = new PostgresTestContext();
        var model = await Service(ctx).GetComparableAsync("Nothing Saved Yet");
        model.Should().BeNull("the compare page shows 'specs not yet profiled' for this");
    }

    [Fact]
    public async Task GetComparable_BuildsSpecsAndOffersFromSavedData()
    {
        using var ctx = new PostgresTestContext();
        var brands = new BrandRepository(ctx.Db);
        var models = new BrandModelRepository(ctx.Db);
        var prices = new ScrapedPriceRepository(ctx.Db);
        var now = DateTimeOffset.UtcNow;

        var brand = await brands.UpsertAsync(new Brand { Name = "Samsung", LastRefreshed = now });
        await models.UpsertAsync(new BrandModel
        {
            BrandId = brand.Id, ModelName = "Galaxy S24",
            SpecsJson = """{ "ram": "8 GB" }""", LastRefreshed = now
        });
        var key = ProductProfile.KeyFor("Samsung", "Galaxy S24", "Galaxy S24");
        await prices.AddAsync(new ScrapedPrice { ProductName = "Galaxy S24", ProductKey = key, StoreName = "SmartBuy",
            Price = 900m, Currency = "JOD", Provider = "context.dev", ScrapedAt = now });

        var model = await Service(ctx).GetComparableAsync("Samsung Galaxy S24");

        model.Should().NotBeNull();
        model!.Specs.Should().ContainKey("ram");
        model.LowestPrice.Should().Be(900m);
        model.Offers.Should().ContainSingle().Which.IsLowest.Should().BeTrue();
    }

    [Fact]
    public async Task GetAsync_PrefersCanonicalFinalSpecs_OverRawHarvestedSpecs()
    {
        using var ctx = new PostgresTestContext();
        var brands = new BrandRepository(ctx.Db);
        var models = new BrandModelRepository(ctx.Db);
        var now = DateTimeOffset.UtcNow;

        var brand = await brands.UpsertAsync(new Brand { Name = "Samsung", LastRefreshed = now });
        var model = await models.UpsertAsync(new BrandModel
        {
            BrandId = brand.Id, ModelName = "Galaxy S24",
            // Raw harvested specs (messy) and the canonical merged sheet the deep-dive produced.
            SpecsJson = """{ "ram": "8GB (raw)" }""",
            FinalSpecsJson = """{ "ram": "8 GB", "storage": "256 GB" }""",
            LastRefreshed = now
        });

        var view = await Service(ctx).GetAsync(model.Id.ToString(), "ignored", "jordan");

        view.Should().NotBeNull();
        view!.Specs.Should().ContainKey("ram").WhoseValue.Should().Be("8 GB", "the UI reads the canonical FinalSpecsJson, not the raw SpecsJson");
        view.Specs.Should().ContainKey("storage").WhoseValue.Should().Be("256 GB");
    }

    [Fact]
    public async Task GetAsync_BuildsSpecTable_FromProfileSpecsJson_WhenNoCatalogueRowExists()
    {
        using var ctx = new PostgresTestContext();
        var profiles = new ProductProfileRepository(ctx.Db);
        var now = DateTimeOffset.UtcNow;

        // The per-item deep-dive persists the canonical sheet to the profile for an item with no harvested
        // BrandModel — this is what stops the "Details not yet available" placeholder for in-pipeline items.
        await profiles.UpsertAsync(new ProductProfile
        {
            Name = "Samsung AR24", Brand = "Samsung", Model = "AR24",
            Details = "A Wind-Free split AC.",
            SpecsJson = """{ "cooling": "24000 BTU", "energy": "A++" }""",
            LastRefreshed = now
        });

        var view = await Service(ctx).GetAsync("p_hash", "Samsung AR24", "jordan");

        view.Should().NotBeNull("an enriched profile alone is enough to render the page");
        view!.HasCatalogueProfile.Should().BeFalse("no harvested BrandModel backs this item");
        view.Description.Should().Be("A Wind-Free split AC.");
        view.Specs.Should().ContainKey("cooling").WhoseValue.Should().Be("24000 BTU");
        view.Specs.Should().ContainKey("energy").WhoseValue.Should().Be("A++");
    }
}
