using Daleel.Search.Abstractions;
using Daleel.Search.Providers;
using FluentAssertions;
using Xunit;

namespace Daleel.Search.Tests;

public class SerpApiProviderTests
{
    private static SerpApiProvider Build(StubHttpMessageHandler handler) =>
        new(apiKey: "k", httpClient: handler.Client(SerpApiProvider.DefaultBaseUrl), delay: (_, _) => Task.CompletedTask);

    [Fact]
    public async Task SearchAsync_Web_ParsesOrganicResults()
    {
        const string json = """
        { "organic_results": [
            { "title": "Best AC in Jordan", "link": "https://a.com", "snippet": "top picks", "position": 1 },
            { "title": "أفضل مكيف", "link": "https://b.com", "snippet": "مراجعات", "position": 2 }
        ] }
        """;
        var provider = Build(new StubHttpMessageHandler(json));

        var results = await provider.SearchAsync(new SearchQuery { Query = "best AC", Kind = SearchKind.Web });

        results.Results.Should().HaveCount(2);
        results.Results[0].Title.Should().Be("Best AC in Jordan");
        results.Results[1].Url.Should().Be("https://b.com");
    }

    [Fact]
    public async Task SearchAsync_Shopping_ParsesPrices()
    {
        const string json = """
        { "shopping_results": [
            { "title": "Samsung AR24", "link": "https://shop/1", "price": "450 JOD", "source": "OpenSooq", "rating": 4.5 }
        ] }
        """;
        var provider = Build(new StubHttpMessageHandler(json));

        var results = await provider.SearchAsync(new SearchQuery { Query = "AC", Kind = SearchKind.Shopping });

        results.Results.Should().ContainSingle();
        var r = results.Results[0];
        r.Price.Should().NotBeNull();
        r.Price!.Value.Amount.Should().Be(450);
        r.Price.Value.Currency.Should().Be("JOD");
        r.Seller.Should().Be("OpenSooq");
    }

    [Fact]
    public async Task SearchAsync_Maps_ParsesLocalResults()
    {
        const string json = """
        { "local_results": [
            { "title": "AC Store Amman", "address": "Downtown", "rating": 4.2, "website": "https://store" }
        ] }
        """;
        var provider = Build(new StubHttpMessageHandler(json));

        var results = await provider.SearchAsync(new SearchQuery { Query = "AC store", Kind = SearchKind.Maps });

        results.Results.Should().ContainSingle();
        results.Results[0].Seller.Should().Be("AC Store Amman");
        results.Results[0].Rating.Should().Be(4.2);
    }

    [Fact]
    public async Task SearchAsync_SendsGeoParams()
    {
        var handler = new StubHttpMessageHandler("""{ "organic_results": [] }""");
        var provider = Build(handler);

        await provider.SearchAsync(new SearchQuery
        {
            Query = "مكيف", Kind = SearchKind.Web, CountryCode = "jo", LanguageCode = "ar"
        });

        var uri = handler.Requests.Single().RequestUri!.ToString();
        uri.Should().Contain("gl=jo").And.Contain("hl=ar").And.Contain("engine=google");
    }

    [Fact]
    public async Task SearchAsync_Web_PaginatesAcrossPagesUpToMaxResults()
    {
        // Each page returns 10 distinct organic results keyed off the start offset, so a 25-result
        // request should walk 3 pages (start=0,10,20) and stop once it has 25.
        var handler = new StubHttpMessageHandler(req =>
        {
            var start = StartOf(req);
            var items = string.Join(",", Enumerable.Range(0, 10).Select(i =>
                $$"""{ "title": "r{{start + i}}", "link": "https://x/{{start + i}}", "position": {{start + i + 1}} }"""));
            return (System.Net.HttpStatusCode.OK, $$"""{ "organic_results": [ {{items}} ] }""");
        });
        var provider = Build(handler);

        var results = await provider.SearchAsync(new SearchQuery
        {
            Query = "best AC", Kind = SearchKind.Web, MaxResults = 25
        });

        results.Results.Should().HaveCount(25);
        handler.Requests.Should().HaveCount(3);
        handler.Requests[0].RequestUri!.ToString().Should().NotContain("start=");
        handler.Requests[1].RequestUri!.ToString().Should().Contain("start=10");
        handler.Requests[2].RequestUri!.ToString().Should().Contain("start=20");
        results.Results.Select(r => r.Url).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task SearchAsync_Web_StopsEarlyOnEmptyPage()
    {
        // First page has results, second is empty: we must stop, not keep hitting all 10 pages.
        var handler = new StubHttpMessageHandler(req =>
            StartOf(req) == 0
                ? (System.Net.HttpStatusCode.OK,
                    """{ "organic_results": [ { "title": "a", "link": "https://a", "position": 1 } ] }""")
                : (System.Net.HttpStatusCode.OK, """{ "organic_results": [] }"""));
        var provider = Build(handler);

        var results = await provider.SearchAsync(new SearchQuery
        {
            Query = "rare", Kind = SearchKind.Web, MaxResults = 100
        });

        results.Results.Should().ContainSingle();
        handler.Requests.Should().HaveCount(2); // page 1 (data) + page 2 (empty ⇒ stop)
    }

    [Fact]
    public async Task SearchAsync_Web_SinglePageWhenMaxResultsSmall()
    {
        var handler = new StubHttpMessageHandler(
            """{ "organic_results": [ { "title": "a", "link": "https://a", "position": 1 } ] }""");
        var provider = Build(handler);

        await provider.SearchAsync(new SearchQuery { Query = "x", Kind = SearchKind.Web, MaxResults = 10 });

        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task SearchAsync_Maps_DoesNotPaginate()
    {
        var handler = new StubHttpMessageHandler(
            """{ "local_results": [ { "title": "Store", "address": "Downtown", "rating": 4.2 } ] }""");
        var provider = Build(handler);

        await provider.SearchAsync(new SearchQuery { Query = "store", Kind = SearchKind.Maps, MaxResults = 100 });

        handler.Requests.Should().ContainSingle();
    }

    /// <summary>Extracts the SerpAPI <c>start</c> offset from a recorded request (0 when absent).</summary>
    private static int StartOf(HttpRequestMessage req)
    {
        var query = req.RequestUri!.Query;
        var match = System.Text.RegularExpressions.Regex.Match(query, "start=(\\d+)");
        return match.Success ? int.Parse(match.Groups[1].Value) : 0;
    }

    [Fact]
    public void Supports_AllKinds()
    {
        var provider = Build(new StubHttpMessageHandler("{}"));
        provider.Supports(SearchKind.Web).Should().BeTrue();
        provider.Supports(SearchKind.Shopping).Should().BeTrue();
        provider.Supports(SearchKind.Maps).Should().BeTrue();
    }
}
