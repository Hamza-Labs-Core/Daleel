using Daleel.Core.Models;
using Daleel.Web.Pipeline.SubWorkflows;
using FluentAssertions;
using Xunit;

namespace Daleel.Web.Tests.Pipeline;

/// <summary>
/// Covers the pure navigation choice the crawler makes from an LLM <see cref="SiteAssessment"/>: which entry
/// point wins for each approach, how the query is injected into a search template, and the fallback order.
/// </summary>
public class CrawlNavigationTests
{
    [Fact]
    public void BuildSearchUrl_UrlEncodesTheQuery()
    {
        CrawlNavigation.BuildSearchUrl("https://x.com/search?q={query}", "portable AC & heater")
            .Should().Be("https://x.com/search?q=portable%20AC%20%26%20heater");
    }

    [Fact]
    public void BuildSearchUrl_NullWhenNoTemplateOrPlaceholder()
    {
        CrawlNavigation.BuildSearchUrl(null, "AC").Should().BeNull();
        CrawlNavigation.BuildSearchUrl("https://x.com/search", "AC").Should().BeNull();
    }

    [Fact]
    public void ResolveEntryPoint_Search_SubstitutesQuery()
    {
        var a = new StoreAssessment
        {
            RecommendedApproach = CrawlApproach.Search,
            SearchUrlTemplate = "https://x.com/search?q={query}",
            ListingUrls = new[] { "https://x.com/all" }
        };
        CrawlNavigation.ResolveEntryPoint(a, "split AC").Should().Be("https://x.com/search?q=split%20AC");
    }

    [Fact]
    public void ResolveEntryPoint_Category_UsesFirstListing()
    {
        var a = new StoreAssessment
        {
            RecommendedApproach = CrawlApproach.Category,
            ListingUrls = new[] { "https://x.com/cat/ac", "https://x.com/cat/all" }
        };
        CrawlNavigation.ResolveEntryPoint(a, "AC").Should().Be("https://x.com/cat/ac");
    }

    [Fact]
    public void ResolveEntryPoint_Api_UsesFirstEndpoint()
    {
        var a = new StoreAssessment
        {
            RecommendedApproach = CrawlApproach.Api,
            ApiEndpoints = new[] { "https://x.com/products.json" }
        };
        CrawlNavigation.ResolveEntryPoint(a, "AC").Should().Be("https://x.com/products.json");
    }

    [Fact]
    public void ResolveEntryPoint_FallsBackThroughEntryPoints_WhenRecommendedIsEmpty()
    {
        // Recommends search, but there's no search template — fall back to the category listing.
        var a = new StoreAssessment
        {
            RecommendedApproach = CrawlApproach.Search,
            ListingUrls = new[] { "https://x.com/cat/ac" },
            SitemapUrl = "https://x.com/sitemap.xml"
        };
        CrawlNavigation.ResolveEntryPoint(a, "AC").Should().Be("https://x.com/cat/ac");
    }

    [Fact]
    public void ResolveEntryPoint_NullWhenNoEntryPoint()
    {
        CrawlNavigation.ResolveEntryPoint(new StoreAssessment(), "AC").Should().BeNull();
    }

    [Fact]
    public void ResolveEntryPoint_Search_StripsGeoWordsFromTheStoreSearchTerm()
    {
        // "diapers Jordan" typed into a store's own search box AND-matches "Jordan" against product
        // titles → the store answers with its no-results page and a stocked store yields nothing. Only
        // the product tokens ("diapers") belong in the search term; "Jordan" is market scope.
        var a = new StoreAssessment
        {
            RecommendedApproach = CrawlApproach.Search,
            SearchUrlTemplate = "https://x.com/search?q={query}"
        };
        CrawlNavigation.ResolveEntryPoint(a, "diapers Jordan", "jordan")
            .Should().Be("https://x.com/search?q=diapers");
    }

    [Fact]
    public void ResolveEntryPoint_Search_StripsTheMarketCentreCity()
    {
        // The raw geo key drops "jordan" but a query can name the market's city instead ("... in Amman").
        var a = new StoreAssessment
        {
            RecommendedApproach = CrawlApproach.Search,
            SearchUrlTemplate = "https://x.com/search?q={query}"
        };
        CrawlNavigation.ResolveEntryPoint(a, "coffee grinder in Amman", "jordan")
            .Should().Be("https://x.com/search?q=coffee%20grinder");
    }

    [Fact]
    public void ResolveEntryPoint_Search_KeepsRawQueryWhenCleaningEmptiesIt()
    {
        // A query that is ALL geo/filler is better searched raw than not at all.
        var a = new StoreAssessment
        {
            RecommendedApproach = CrawlApproach.Search,
            SearchUrlTemplate = "https://x.com/search?q={query}"
        };
        CrawlNavigation.ResolveEntryPoint(a, "Jordan", "jordan")
            .Should().Be("https://x.com/search?q=Jordan");
    }
}
