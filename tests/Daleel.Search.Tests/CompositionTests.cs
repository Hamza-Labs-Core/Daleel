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
}
