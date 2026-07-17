using Daleel.Core.Geo;
using Daleel.Core.Models;
using FluentAssertions;
using Xunit;

namespace Daleel.Agent.Tests;

/// <summary>
/// Covers the three specialised crawlers' LLM reasoning: pure DTO→model mapping + URL absolutization (no LLM),
/// and the metered LLM calls (store assess/extract, brand assess/extract, product detail, classify) driven by
/// a deterministic <see cref="FakeLlmClient"/> routed on system prompt.
/// </summary>
public class SiteCrawlTests
{
    private static readonly Uri Origin = new("https://shop.example.com/");
    private static GeoProfile Geo => GeoProfiles.ResolveOrDefault("jordan");

    // ── Pure mapping / absolutization ────────────────────────────────────────────

    [Fact]
    public void MapStoreAssessment_AbsolutizesUrls_ParsesApproach_KeepsSearchPlaceholder()
    {
        var dto = new AgentService.StoreAssessmentDto
        {
            Platform = "Shopify",
            ListingUrls = new() { "/collections/air-conditioners", "https://shop.example.com/collections/all" },
            SearchUrl = "/search?q={query}",
            SitemapUrl = "/sitemap.xml",
            ApiEndpoints = new() { "/products.json" },
            Approach = "api"
        };

        var a = AgentService.MapStoreAssessment(dto, Origin);

        a.Platform.Should().Be("Shopify");
        a.RecommendedApproach.Should().Be(CrawlApproach.Api);
        a.ListingUrls.Should().Contain("https://shop.example.com/collections/air-conditioners");
        a.ApiEndpoints.Should().ContainSingle().Which.Should().Be("https://shop.example.com/products.json");
        a.SitemapUrl.Should().Be("https://shop.example.com/sitemap.xml");
        a.SearchUrlTemplate.Should().Be("https://shop.example.com/search?q={query}");
        a.HasEntryPoint.Should().BeTrue();
    }

    [Fact]
    public void MapBrandCatalog_AbsolutizesCatalogAndLines()
    {
        var dto = new AgentService.BrandCatalogDto
        {
            CatalogUrl = "/products",
            ProductLineUrls = new() { "/products/tvs/oled", "https://shop.example.com/products/tvs/qned" },
            Platform = "AEM"
        };

        var b = AgentService.MapBrandCatalog(dto, Origin);

        b.CatalogUrl.Should().Be("https://shop.example.com/products");
        b.ProductLineUrls.Should().HaveCount(2);
        b.HasCatalog.Should().BeTrue();
        b.EntryPoints.First().Should().Be("https://shop.example.com/products"); // catalogue landing first
    }

    [Theory]
    [InlineData("/collections/all", "https://shop.example.com/collections/all")]
    [InlineData("https://other.com/x", "https://other.com/x")]
    [InlineData("#reviews", null)]
    [InlineData("javascript:void(0)", null)]
    public void AbsolutizeUrl_ResolvesRelative_AndRejectsJunk(string input, string? expected)
    {
        AgentService.AbsolutizeUrl(input, Origin).Should().Be(expected);
    }

    [Fact]
    public void ApplyDropIndices_RemovesListed_IgnoresOutOfRange()
    {
        var items = new[] { "a", "b", "c", "d" };
        AgentService.ApplyDropIndices(items, new[] { 1, 3 }).Should().Equal("a", "c");
        AgentService.ApplyDropIndices(items, new[] { -1, 99 }).Should().Equal("a", "b", "c", "d");
        AgentService.ApplyDropIndices(items, System.Array.Empty<int>()).Should().BeSameAs(items);
    }

    // ── STORE crawler ────────────────────────────────────────────────────────────

    [Fact]
    public async Task AssessStoreAsync_ParsesPlatformAndEntryPoints()
    {
        const string json = """
            { "platform": "WooCommerce", "listingUrls": ["/shop/cooling"], "searchUrl": "/?s={query}",
              "apiEndpoints": [], "approach": "search", "notes": "has search" }
            """;
        var agent = new AgentService(new FakeLlmClient(system =>
            system.Contains("e-commerce store navigator") ? json : "{}"));

        var a = await agent.AssessStoreAsync("https://shop.example.com", "# Home", "portable AC");

        a.Platform.Should().Be("WooCommerce");
        a.RecommendedApproach.Should().Be(CrawlApproach.Search);
        a.SearchUrlTemplate.Should().Be("https://shop.example.com/?s={query}");
    }

    [Fact]
    public async Task ExtractStoreListingAsync_ExtractsPricedCards_AndAbsolutizesUrls()
    {
        const string json = """
            { "products": [
                { "name": "Gree Pular 12000", "brand": "Gree", "model": "GWH12", "url": "/p/gree-12k",
                  "imageUrl": "/img/gree.jpg", "price": 320, "currency": "JOD", "availability": "in stock" },
                { "name": "https://junk.com", "price": 1 }
              ] }
            """;
        var agent = new AgentService(new FakeLlmClient(system =>
            system.Contains("product CARDS") ? json : """{"nextPageUrl":null}"""));

        var r = await agent.ExtractStoreListingAsync("# listing", "https://shop.example.com/shop", "AC", Geo);

        r.Products.Should().ContainSingle(); // the URL-as-name junk card is dropped
        var p = r.Products[0];
        p.Name.Should().Be("Gree Pular 12000");
        p.Price.Should().Be(320m);
        p.Url.Should().Be("https://shop.example.com/p/gree-12k");
        p.ImageUrl.Should().Be("https://shop.example.com/img/gree.jpg");
    }

    // ── BRAND crawler ────────────────────────────────────────────────────────────

    [Fact]
    public async Task AssessBrandCatalogAsync_FindsCatalogAndLines()
    {
        const string json = """
            { "catalogUrl": "/products", "productLineUrls": ["/products/tvs/oled"], "platform": null,
              "notes": "catalogue under /products" }
            """;
        var agent = new AgentService(new FakeLlmClient(system =>
            system.Contains("brand/manufacturer website navigator") ? json : "{}"));

        var b = await agent.AssessBrandCatalogAsync("https://lg.example.com", "# LG", "TV");

        b.CatalogUrl.Should().Be("https://lg.example.com/products");
        b.ProductLineUrls.Should().ContainSingle().Which.Should().Be("https://lg.example.com/products/tvs/oled");
        b.HasCatalog.Should().BeTrue();
    }

    [Fact]
    public async Task ExtractBrandModelsAsync_ExtractsModelsWithSpecs()
    {
        const string json = """
            { "products": [
                { "name": "OLED evo C4", "brand": "LG", "model": "OLED55C4", "url": "/products/c4",
                  "specs": { "panel": "OLED evo", "size": "55\"" } }
              ] }
            """;
        var agent = new AgentService(new FakeLlmClient(system =>
            system.Contains("product MODELS") ? json : """{"nextPageUrl":null}"""));

        var r = await agent.ExtractBrandModelsAsync("# oled", "https://lg.example.com/products/tvs", "TV", Geo);

        r.Products.Should().ContainSingle();
        var m = r.Products[0];
        m.Model.Should().Be("OLED55C4");
        m.Price.Should().BeNull(); // brand pages usually have no price
        m.Specs.Should().ContainKey("panel");
    }

    // ── PRODUCT DETAIL ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ExtractProductDetailAsync_FoldsFullRecord_KeepsOfferPrice_UpgradesName()
    {
        const string json = """
            {
              "name": "Gree Pular 12000 BTU Inverter", "brand": "Gree", "sku": "GWH12",
              "images": ["/img/1.jpg", "/img/2.jpg"], "price": 340, "currency": "JOD",
              "availability": "in stock", "description": "Inverter split AC",
              "specs": { "capacity": "12000 BTU" }, "features": ["WiFi", "Turbo"],
              "relatedProducts": ["Gree 18000"], "reviews": [ { "text": "Great", "rating": 5 } ],
              "seller": "Example Shop"
            }
            """;
        var agent = new AgentService(new FakeLlmClient(system =>
            system.Contains("COMPLETE record") ? json : "{}"));

        var listing = new ProductListing { Name = "Gree AC", Price = 320m, Url = "https://shop.example.com/p/gree" };
        var (folded, detail) = await agent.ExtractProductDetailAsync("# Gree", listing, Geo);

        detail.Should().NotBeNull();
        detail!.Images.Should().HaveCount(2);
        detail.Reviews.Should().ContainSingle();
        folded.Name.Should().Be("Gree Pular 12000 BTU Inverter"); // detail page has the fuller name
        folded.Price.Should().Be(320m);                            // existing offer price kept
        folded.Brand.Should().Be("Gree");
        folded.Sku.Should().Be("GWH12");
        folded.Specs.Should().ContainKey("capacity");
        folded.Specs.Should().ContainKey("features");
        folded.Specs.Should().ContainKey("related_products");
        folded.RatedReviews.Should().ContainSingle();
    }

    [Fact]
    public async Task ExtractProductDetailAsync_ReturnsListingUnchanged_OnFailure()
    {
        var agent = new AgentService(new FakeLlmClient(_ => "garbage"));
        var listing = new ProductListing { Name = "X", Url = "https://shop.example.com/p/x" };

        var (folded, detail) = await agent.ExtractProductDetailAsync("# X", listing, Geo);
        detail.Should().BeNull();
        folded.Should().BeSameAs(listing);
    }

    // ── Shared: classify ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ClassifyListingsAsync_DropsFlaggedItems()
    {
        var agent = new AgentService(new FakeLlmClient(system =>
            system == PromptTemplates.RelevanceGateSystem ? """{ "drop": [1] }""" : "{}"));

        var listings = new[]
        {
            new ProductListing { Name = "Gree AC 12000" },
            new ProductListing { Name = "AC remote control" },
            new ProductListing { Name = "Samsung AC 18000" },
            new ProductListing { Name = "LG AC 24000" }
        };

        var kept = await agent.ClassifyListingsAsync("air conditioner", listings);
        kept.Should().HaveCount(3);
        kept.Select(l => l.Name).Should().NotContain("AC remote control");
    }

    [Fact]
    public async Task ClassifyListingsAsync_IgnoresImplausibleWipeout()
    {
        var agent = new AgentService(new FakeLlmClient(system =>
            system == PromptTemplates.RelevanceGateSystem ? """{ "drop": [0, 1, 2] }""" : "{}"));

        var listings = new[]
        {
            new ProductListing { Name = "Gree AC 12000" },
            new ProductListing { Name = "Samsung AC 18000" },
            new ProductListing { Name = "LG AC 24000" }
        };

        (await agent.ClassifyListingsAsync("air conditioner", listings)).Should().HaveCount(3);
    }

    [Fact]
    public async Task ClassifyListingsAsync_TrustsLopsidedDrop_OnACatalogueCrawl()
    {
        // A brand-catalogue crawl for "air conditioner" lands on a page that really is mostly fridges.
        // Dropping 11 of 12 is the gate WORKING. The old ratio guard (kept < 12/5) called that
        // implausible and put every fridge back on the grid — the "LG fridge in an AC search" leak.
        var drop = string.Join(", ", Enumerable.Range(1, 11));
        var agent = new AgentService(new FakeLlmClient(system =>
            system == PromptTemplates.RelevanceGateSystem ? $$"""{ "drop": [{{drop}}] }""" : "{}"));

        var listings = new[] { "LG AC 24000" }
            .Concat(Enumerable.Range(1, 11).Select(i => $"LG Refrigerator {i}00L"))
            .Select(n => new ProductListing { Name = n })
            .ToList();

        var kept = await agent.ClassifyListingsAsync("air conditioner", listings);

        kept.Select(l => l.Name).Should().Equal(new[] { "LG AC 24000" },
            "a verdict that keeps something cannot empty the grid, so it must be trusted however lopsided");
    }

    [Fact]
    public async Task ClassifyListingsAsync_TrustsTotalWipeout_OnALargeSet()
    {
        // Nine straight fridges for an AC query is a real answer ("this catalogue has no ACs"), not a
        // misfire. Only a SMALL set gets the benefit of the doubt — the other stores still fill the grid.
        var agent = new AgentService(new FakeLlmClient(system =>
            system == PromptTemplates.RelevanceGateSystem
                ? """{ "drop": [0, 1, 2, 3, 4, 5, 6, 7, 8] }"""
                : "{}"));

        var listings = Enumerable.Range(1, 9)
            .Select(i => new ProductListing { Name = $"LG Refrigerator {i}00L" })
            .ToList();

        (await agent.ClassifyListingsAsync("air conditioner", listings)).Should().BeEmpty();
    }
}
