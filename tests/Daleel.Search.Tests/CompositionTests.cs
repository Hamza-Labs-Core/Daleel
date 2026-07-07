using System.Text.Json;
using Daleel.Core.Models;
using Daleel.Search;
using Daleel.Search.Abstractions;
using Daleel.Search.Providers;
using FluentAssertions;
using Xunit;

namespace Daleel.Search.Tests;

/// <summary>A fake search engine returning canned shopping results.</summary>
internal sealed class FakeSearchProvider : ISearchProvider
{
    private readonly IReadOnlyList<SearchResult> _results;
    public string Name => "fake-search";
    public FakeSearchProvider(params SearchResult[] results) => _results = results;
    public bool Supports(SearchKind kind) => true;
    public Task<SearchResults> SearchAsync(SearchQuery query, CancellationToken ct = default) =>
        Task.FromResult(new SearchResults { Provider = Name, Query = query.Query, Kind = query.Kind, Results = _results });
}

public class GoogleShoppingProviderTests
{
    [Fact]
    public async Task SearchPricesAsync_ReturnsOnlyPricedResults()
    {
        var engine = new FakeSearchProvider(
            new SearchResult { Title = "AC A", Price = new Money(450, "JOD"), Seller = "X", Kind = SearchKind.Shopping },
            new SearchResult { Title = "AC B (no price)", Kind = SearchKind.Shopping });

        var shopping = new GoogleShoppingProvider(engine, "JOD");
        var prices = await shopping.SearchPricesAsync("AC");

        prices.Should().ContainSingle();
        prices[0].Price.Amount.Should().Be(450);
        prices[0].Store.Should().Be("X");
    }

    [Fact]
    public void Constructor_RejectsNonShoppingEngine()
    {
        var nonShopping = new NonShoppingEngine();
        var act = () => new GoogleShoppingProvider(nonShopping);
        act.Should().Throw<ArgumentException>();
    }

    private sealed class NonShoppingEngine : ISearchProvider
    {
        public string Name => "web-only";
        public bool Supports(SearchKind kind) => kind == SearchKind.Web;
        public Task<SearchResults> SearchAsync(SearchQuery query, CancellationToken ct = default) =>
            Task.FromResult(SearchResults.Empty(Name, query.Query, query.Kind));
    }
}

public class ScrapeRouterTests
{
    private sealed class StubScraper : IScrapeProvider
    {
        private readonly ScrapedPage _page;
        private readonly bool _throws;
        public string Name { get; }
        public StubScraper(string name, ScrapedPage page, bool throws = false)
        {
            Name = name; _page = page; _throws = throws;
        }
        public Task<ScrapedPage> ScrapeAsync(string url, ScrapeFormat format = ScrapeFormat.Markdown, CancellationToken ct = default)
        {
            if (_throws) throw new InvalidOperationException("boom");
            return Task.FromResult(_page);
        }
    }

    [Fact]
    public async Task ScrapeAsync_ReturnsFirstSuccess()
    {
        var router = new ScrapeRouter(
            new StubScraper("primary", new ScrapedPage { Content = "good", Success = true }),
            new StubScraper("fallback", new ScrapedPage { Content = "unused", Success = true }));

        var page = await router.ScrapeAsync("https://x");
        page.Content.Should().Be("good");
    }

    [Fact]
    public async Task ScrapeAsync_FallsBackOnEmptyContent()
    {
        var router = new ScrapeRouter(
            new StubScraper("primary", new ScrapedPage { Content = "", Success = false }),
            new StubScraper("fallback", new ScrapedPage { Content = "recovered", Success = true }));

        var page = await router.ScrapeAsync("https://x");
        page.Content.Should().Be("recovered");
    }

    [Fact]
    public async Task ScrapeAsync_FallsBackOnException()
    {
        var router = new ScrapeRouter(
            new StubScraper("primary", new ScrapedPage(), throws: true),
            new StubScraper("fallback", new ScrapedPage { Content = "recovered", Success = true }));

        var page = await router.ScrapeAsync("https://x");
        page.Success.Should().BeTrue();
        page.Content.Should().Be("recovered");
    }

    [Fact]
    public async Task ScrapeAsync_AllFail_ReturnsFailure()
    {
        var router = new ScrapeRouter(
            new StubScraper("primary", new ScrapedPage { Success = false }),
            new StubScraper("fallback", new ScrapedPage { Success = false }));

        var page = await router.ScrapeAsync("https://x");
        page.Success.Should().BeFalse();
    }

    // A chain member that can both scrape and extract (Context.dev / Cloudflare-browser shape).
    private sealed class StubExtractScraper : IScrapeProvider, IExtractProvider
    {
        private readonly string _json;
        private readonly bool _throws;
        public string Name { get; }
        public int ExtractCalls { get; private set; }
        public StubExtractScraper(string name, string json, bool throws = false)
            => (Name, _json, _throws) = (name, json, throws);

        public Task<ScrapedPage> ScrapeAsync(string url, ScrapeFormat format = ScrapeFormat.Markdown, CancellationToken ct = default)
            => Task.FromResult(new ScrapedPage { Content = "md", Success = true, Provider = Name });

        public Task<JsonElement> ExtractAsync(string url, object jsonSchema, CancellationToken ct = default)
        {
            ExtractCalls++;
            if (_throws) throw new InvalidOperationException("boom");
            using var doc = JsonDocument.Parse(_json);
            return Task.FromResult(doc.RootElement.Clone());
        }
    }

    private static string? FirstProductName(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty("products", out var arr) &&
        arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0
            ? arr[0].GetProperty("name").GetString()
            : null;

    [Fact]
    public async Task ExtractAsync_PrefersTheBrowser_ReverseOfScrapeOrder()
    {
        // The chain is cheap-first for scraping (context → browser); structured extraction of a
        // store page prefers the most capable renderer — the browser — i.e. the reverse.
        var router = new ScrapeRouter(
            new StubExtractScraper("context", """{ "products": [ { "name": "from-context" } ] }"""),
            new StubExtractScraper("browser", """{ "products": [ { "name": "from-browser" } ] }"""));

        var result = await router.ExtractAsync("https://store/x", new { });
        FirstProductName(result).Should().Be("from-browser");
    }

    [Fact]
    public async Task ExtractAsync_FallsBackToLighterExtractor_WhenBrowserEmpty()
    {
        var router = new ScrapeRouter(
            new StubExtractScraper("context", """{ "products": [ { "name": "from-context" } ] }"""),
            new StubExtractScraper("browser", """{ "products": [] }"""));

        var result = await router.ExtractAsync("https://store/x", new { });
        FirstProductName(result).Should().Be("from-context");
    }

    [Fact]
    public async Task ExtractAsync_SkipsNonExtractMembers_AndStillSurfacesProducts()
    {
        // The bug this fixes: a plain (scrape-only) provider in the chain must NOT hide the
        // extract-capable member — before the router forwarded IExtractProvider, it did exactly that.
        var router = new ScrapeRouter(
            new StubScraper("plain", new ScrapedPage { Content = "md", Success = true }),
            new StubExtractScraper("browser", """{ "products": [ { "name": "from-browser" } ] }"""));

        var result = await router.ExtractAsync("https://store/x", new { });
        FirstProductName(result).Should().Be("from-browser");
    }

    [Fact]
    public async Task ExtractAsync_FallsBackOnThrow()
    {
        var router = new ScrapeRouter(
            new StubExtractScraper("context", """{ "products": [ { "name": "from-context" } ] }"""),
            new StubExtractScraper("browser", "{}", throws: true));

        var result = await router.ExtractAsync("https://store/x", new { });
        FirstProductName(result).Should().Be("from-context");
    }

    [Fact]
    public async Task ExtractAsync_AcceptsAlternateArrayKeys_WithoutBillingTheFallback()
    {
        // A result wrapped under "items" (a key ListingExtractor.ResolveProductsArray accepts) must
        // count as non-empty, so the router short-circuits on the browser and never bills the fallback
        // extractor — HasProducts must stay in lockstep with the consumer's accepted keys.
        var context = new StubExtractScraper("context", """{ "products": [ { "name": "from-context" } ] }""");
        var browser = new StubExtractScraper("browser", """{ "items": [ { "name": "from-browser" } ] }""");
        var router = new ScrapeRouter(context, browser); // extract reverses → browser first

        var result = await router.ExtractAsync("https://store/x", new { });

        result.TryGetProperty("items", out var items).Should().BeTrue("the browser's items-wrapped result is returned as-is");
        items.GetArrayLength().Should().Be(1);
        browser.ExtractCalls.Should().Be(1);
        context.ExtractCalls.Should().Be(0, "the browser result was non-empty, so the fallback is never called or billed");
    }
}
