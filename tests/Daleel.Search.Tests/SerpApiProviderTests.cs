using Daleel.Search.Abstractions;
using Daleel.Search.Http;
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
    public async Task SearchAsync_Web_CappedSoftBody_YieldsEmptyWithoutThrowing()
    {
        // When the search-worker's account-wide hourly SerpAPI cap trips, it returns HTTP 200 with a
        // structurally-valid but EMPTY SerpAPI body (never a 429/5xx). The provider must parse that as
        // zero results and its paged loop must break cleanly — a throw here would fault the whole
        // search into a false "no results". This locks in the soft-cap contract the worker relies on.
        const string capped = """
        { "search_metadata": { "status": "Capped" },
          "organic_results": [], "shopping_results": [], "local_results": [], "images_results": [] }
        """;
        var handler = new StubHttpMessageHandler(capped);
        var provider = Build(handler);

        var results = await provider.SearchAsync(
            new SearchQuery { Query = "best AC", Kind = SearchKind.Web, MaxResults = 100 });

        results.Results.Should().BeEmpty();
        // The empty first page ends paging immediately — no runaway toward MaxPages.
        handler.Requests.Should().ContainSingle();
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
    public async Task SearchAsync_Shopping_UnsupportedGlCountry_LearnsAndStopsCalling()
    {
        // SerpAPI hard-rejects countries Google Shopping doesn't operate in (prod hit this for
        // gl=jo — four doomed paid calls per Jordan search). The first rejection must be learned
        // so later shopping queries for that market return empty WITHOUT another HTTP call.
        // A unique country code keeps this test independent of the provider's static learned set.
        var handler = new StubHttpMessageHandler(req =>
            req.RequestUri!.Query.Contains("engine=google_shopping")
                ? (System.Net.HttpStatusCode.BadRequest, """{ "error": "Unsupported `zz` country - gl parameter." }""")
                : (System.Net.HttpStatusCode.OK, """{ "organic_results": [] }"""));
        var provider = Build(handler);

        var first = await provider.SearchAsync(
            new SearchQuery { Query = "AC", Kind = SearchKind.Shopping, CountryCode = "zz" });
        first.Results.Should().BeEmpty();
        handler.Requests.Should().HaveCount(1);

        var second = await provider.SearchAsync(
            new SearchQuery { Query = "fridge", Kind = SearchKind.Shopping, CountryCode = "zz" });
        second.Results.Should().BeEmpty();
        handler.Requests.Should().HaveCount(1, "the learned unsupported country must not be queried again");

        // Other kinds for the same country are unaffected (web search supports every gl).
        var web = await provider.SearchAsync(
            new SearchQuery { Query = "AC", Kind = SearchKind.Web, CountryCode = "zz" });
        handler.Requests.Should().HaveCountGreaterThan(1, "web searches must still go out");
    }

    [Fact]
    public async Task SearchAsync_Images_ParsesImageResults()
    {
        const string json = """
        { "images_results": [
            { "title": "Samsung AR24 photo", "link": "https://page/1", "thumbnail": "https://encrypted-tbn0.gstatic.com/images?q=1", "original": "https://cdn/1.jpg", "position": 1 },
            { "title": "no image fields at all", "link": "https://page/2", "position": 2 }
        ] }
        """;
        var handler = new StubHttpMessageHandler(json);
        var provider = Build(handler);

        var results = await provider.SearchAsync(new SearchQuery { Query = "Samsung AR24", Kind = SearchKind.Images });

        // The gstatic thumbnail wins (hotlink-safe); an entry with no image fields is skipped.
        results.Results.Should().ContainSingle();
        results.Results[0].ImageUrl.Should().Be("https://encrypted-tbn0.gstatic.com/images?q=1");
        results.Results[0].Kind.Should().Be(SearchKind.Images);
        handler.Requests[0].RequestUri!.Query.Should().Contain("engine=google_images");
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
    public async Task SearchAsync_StalledAttempt_IsBoundedByPerAttemptTimeout_AndFailsTransient()
    {
        // A stalled SerpAPI call (or a stalled edge search-worker proxying it) used to ride
        // HttpClient's 100s default across all three attempts — ~5 min — before SearchRouter could
        // fail over. With a per-attempt cap each attempt aborts fast and the provider surfaces a
        // TRANSIENT ProviderException, which the router treats exactly like an empty result and fails
        // over on. A 50ms cap keeps the test instant; the stub honours the cancellation token.
        using var handler = new HangingHttpMessageHandler();
        var provider = new SerpApiProvider(
            apiKey: "k",
            httpClient: new HttpClient(handler) { BaseAddress = new Uri(SerpApiProvider.DefaultBaseUrl) },
            delay: (_, _) => Task.CompletedTask, // skip the real backoff between retries
            perAttemptTimeout: TimeSpan.FromMilliseconds(50));

        var act = () => provider.SearchAsync(new SearchQuery { Query = "x", Kind = SearchKind.Web, MaxResults = 5 });

        var thrown = await act.Should().ThrowAsync<ProviderException>();
        thrown.Which.IsTransient.Should().BeTrue();
        // Initial attempt + 2 retries, each aborted by the per-attempt timeout — never a 100s hang.
        handler.Attempts.Should().Be(3);
    }

    /// <summary>A handler that never answers within the per-attempt window: it awaits far longer than
    /// any test timeout but honours the linked cancellation token, so a per-attempt timeout aborts it
    /// immediately. Records how many attempts were started.</summary>
    private sealed class HangingHttpMessageHandler : HttpMessageHandler
    {
        private int _attempts;
        public int Attempts => _attempts;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _attempts);
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        }
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
