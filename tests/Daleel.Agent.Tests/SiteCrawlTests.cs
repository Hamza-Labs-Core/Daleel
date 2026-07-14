using Daleel.Core.Geo;
using Daleel.Core.Models;
using FluentAssertions;
using Xunit;

namespace Daleel.Agent.Tests;

/// <summary>
/// Covers the LLM site-crawl reasoning: the pure DTO→model mapping + URL absolutization (no LLM), and the
/// metered LLM calls (assess / deep-dive / classify) driven by a deterministic <see cref="FakeLlmClient"/>
/// routed on system prompt.
/// </summary>
public class SiteCrawlTests
{
    private static readonly Uri Origin = new("https://shop.example.com/");

    // ── Pure mapping / absolutization ────────────────────────────────────────────

    [Fact]
    public void MapAssessment_AbsolutizesUrls_ParsesEnums_AndKeepsSearchPlaceholder()
    {
        var dto = new AgentService.SiteAssessmentDto
        {
            Kind = "store",
            Platform = "Shopify",
            ListingUrls = new() { "/collections/air-conditioners", "https://shop.example.com/collections/all" },
            SearchUrl = "/search?q={query}",
            SitemapUrl = "/sitemap.xml",
            ApiEndpoints = new() { "/products.json" },
            Approach = "api",
            Notes = "Shopify store — use the products API"
        };

        var a = AgentService.MapAssessment(dto, Origin);

        a.Kind.Should().Be(SiteKind.Store);
        a.Platform.Should().Be("Shopify");
        a.RecommendedApproach.Should().Be(CrawlApproach.Api);
        a.ListingUrls.Should().Contain("https://shop.example.com/collections/air-conditioners");
        a.ApiEndpoints.Should().ContainSingle().Which.Should().Be("https://shop.example.com/products.json");
        a.SitemapUrl.Should().Be("https://shop.example.com/sitemap.xml");
        // The {query} placeholder must survive absolutization un-encoded.
        a.SearchUrlTemplate.Should().Be("https://shop.example.com/search?q={query}");
        a.HasEntryPoint.Should().BeTrue();
    }

    [Fact]
    public void MapAssessment_DropsSearchTemplateWithoutPlaceholder()
    {
        var dto = new AgentService.SiteAssessmentDto { Kind = "brand", SearchUrl = "/search" };
        var a = AgentService.MapAssessment(dto, Origin);

        a.Kind.Should().Be(SiteKind.Brand);
        a.SearchUrlTemplate.Should().BeNull(); // no {query} ⇒ not a usable template
    }

    [Theory]
    [InlineData("/collections/all", "https://shop.example.com/collections/all")]
    [InlineData("https://other.com/x", "https://other.com/x")]
    [InlineData("#reviews", null)]
    [InlineData("javascript:void(0)", null)]
    [InlineData("mailto:a@b.com", null)]
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

    // ── LLM-driven: assess ───────────────────────────────────────────────────────

    [Fact]
    public async Task AssessSiteAsync_ParsesAndAbsolutizesTheAssessment()
    {
        const string json = """
            {
              "kind": "store",
              "platform": "WooCommerce",
              "listingUrls": ["/shop/cooling"],
              "searchUrl": "/?s={query}",
              "sitemapUrl": null,
              "apiEndpoints": [],
              "approach": "search",
              "notes": "has a working search"
            }
            """;
        var agent = new AgentService(new FakeLlmClient(system =>
            system.Contains("web-crawl navigator") ? json : "{}"));

        var a = await agent.AssessSiteAsync("https://shop.example.com", "# Home\nWelcome", "portable AC");

        a.Kind.Should().Be(SiteKind.Store);
        a.Platform.Should().Be("WooCommerce");
        a.RecommendedApproach.Should().Be(CrawlApproach.Search);
        a.SearchUrlTemplate.Should().Be("https://shop.example.com/?s={query}");
        a.ListingUrls.Should().ContainSingle().Which.Should().Be("https://shop.example.com/shop/cooling");
    }

    [Fact]
    public async Task AssessSiteAsync_ReturnsEmpty_OnUnparseableReply()
    {
        var agent = new AgentService(new FakeLlmClient(_ => "not json at all"));
        var a = await agent.AssessSiteAsync("https://shop.example.com", "# Home", "AC");

        a.Kind.Should().Be(SiteKind.Unknown);
        a.HasEntryPoint.Should().BeFalse();
    }

    [Fact]
    public async Task AssessSiteAsync_ReturnsEmpty_OnBlankMarkdown()
    {
        var agent = new AgentService(new FakeLlmClient(_ => "{}"));
        (await agent.AssessSiteAsync("https://shop.example.com", "   ", "AC")).HasEntryPoint.Should().BeFalse();
    }

    // ── LLM-driven: deep-dive ────────────────────────────────────────────────────

    [Fact]
    public async Task DeepDiveProductAsync_MergesFields_ButPrefersExistingValues()
    {
        const string json = """
            {
              "name": "Gree Pular 12000 BTU",
              "brand": "Gree",
              "sku": "GWH12",
              "images": ["https://cdn.example.com/gree.jpg"],
              "price": 320,
              "currency": "JOD",
              "availability": "in stock",
              "description": "Inverter split AC",
              "specs": { "capacity": "12000 BTU" },
              "seller": "Example Shop"
            }
            """;
        var agent = new AgentService(new FakeLlmClient(system =>
            system.Contains("product's full details") ? json : "{}"));

        // The listing already knows a price; the deep-dive must keep it (the offer price is authoritative)
        // but UPGRADE the name to the fuller detail-page name and fill every gap.
        var listing = new ProductListing { Name = "Gree AC", Price = 300m, Url = "https://shop.example.com/p/gree" };
        var full = await agent.DeepDiveProductAsync("# Gree\n...", listing, GeoProfiles.ResolveOrDefault("jordan"));

        full.Name.Should().Be("Gree Pular 12000 BTU"); // detail page has the fuller, authoritative name
        full.Price.Should().Be(300m);                   // existing offer price kept
        full.Brand.Should().Be("Gree");        // gap filled
        full.Sku.Should().Be("GWH12");
        full.Currency.Should().Be("JOD");
        full.Availability.Should().Be("in stock");
        full.ImageUrl.Should().Be("https://cdn.example.com/gree.jpg");
        full.Specs.Should().ContainKey("capacity");
        full.Specs.Should().ContainKey("description");
    }

    [Fact]
    public async Task DeepDiveProductAsync_ReturnsListingUnchanged_OnFailure()
    {
        var agent = new AgentService(new FakeLlmClient(_ => "garbage"));
        var listing = new ProductListing { Name = "X", Url = "https://shop.example.com/p/x" };

        (await agent.DeepDiveProductAsync("# X", listing, GeoProfiles.ResolveOrDefault("jordan")))
            .Should().BeSameAs(listing);
    }

    // ── LLM-driven: classify ─────────────────────────────────────────────────────

    [Fact]
    public async Task ClassifyListingsAsync_DropsFlaggedItems()
    {
        // Relevance gate replies with the indices to drop.
        var agent = new AgentService(new FakeLlmClient(system =>
            system == PromptTemplates.RelevanceGateSystem ? """{ "drop": [1] }""" : "{}"));

        var listings = new[]
        {
            new ProductListing { Name = "Gree AC 12000" },
            new ProductListing { Name = "AC remote control" }, // accessory — dropped
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
        // Dropping (nearly) everything is treated as a gate misfire — keep the originals.
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
}
