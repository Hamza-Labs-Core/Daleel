using Daleel.Search.Abstractions;
using FluentAssertions;
using Xunit;

namespace Daleel.Search.Tests;

public class SearchRouterTests
{
    /// <summary>A scriptable <see cref="ISearchProvider"/>: canned response, call count, optional Supports.</summary>
    private sealed class FakeProvider : ISearchProvider
    {
        private readonly Func<SearchQuery, SearchResults> _respond;
        private readonly Func<SearchKind, bool> _supports;

        public FakeProvider(string name, Func<SearchQuery, SearchResults> respond, Func<SearchKind, bool>? supports = null)
        {
            Name = name;
            _respond = respond;
            _supports = supports ?? (_ => true);
        }

        public string Name { get; }
        public int Calls { get; private set; }
        public bool Supports(SearchKind kind) => _supports(kind);

        public Task<SearchResults> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(_respond(query));
        }
    }

    private static SearchResults Hits(string provider, params string[] urls) => new()
    {
        Provider = provider,
        Results = urls.Select(u => new SearchResult { Url = u, Source = provider }).ToArray()
    };

    private static SearchResults Empty(string provider) => SearchResults.Empty(provider, "q", SearchKind.Web);

    private static FakeProvider Throws(string name) =>
        new(name, _ => throw new InvalidOperationException("quota exhausted"));

    private static SearchQuery Web => new() { Query = "q", Kind = SearchKind.Web };

    [Fact]
    public async Task ReturnsFirstProviderWithResults_WithoutCallingRest()
    {
        var fallback = new FakeProvider("browser-serp", _ => Hits("browser-serp", "https://b"));
        var router = new SearchRouter(
            new FakeProvider("serpapi", _ => Hits("serpapi", "https://a")), fallback);

        var results = await router.SearchAsync(Web);

        results.Provider.Should().Be("serpapi");
        results.Results.Should().ContainSingle();
        fallback.Calls.Should().Be(0, "the primary already returned results");
    }

    [Fact]
    public async Task FallsThroughOnThrow()
    {
        var router = new SearchRouter(
            Throws("serpapi"), new FakeProvider("browser-serp", _ => Hits("browser-serp", "https://b")));

        var results = await router.SearchAsync(Web);

        results.Provider.Should().Be("browser-serp");
        results.Results.Should().ContainSingle();
    }

    [Fact]
    public async Task FallsThroughOnEmpty()
    {
        var router = new SearchRouter(
            new FakeProvider("serpapi", _ => Empty("serpapi")),
            new FakeProvider("browser-serp", _ => Hits("browser-serp", "https://b")));

        var results = await router.SearchAsync(Web);

        results.Provider.Should().Be("browser-serp");
    }

    [Fact]
    public async Task InvokesFailoverHookOnEachHop()
    {
        var hops = new List<SearchFailover>();
        var router = new SearchRouter(
            new ISearchProvider[]
            {
                Throws("serpapi"),
                new FakeProvider("bing", _ => Empty("bing")),
                new FakeProvider("browser-serp", _ => Hits("browser-serp", "https://b"))
            },
            hops.Add);

        await router.SearchAsync(Web);

        hops.Should().HaveCount(2);
        hops[0].FromProvider.Should().Be("serpapi");
        hops[0].ToProvider.Should().Be("bing");
        hops[0].Reason.Should().Contain("quota exhausted");
        hops[1].FromProvider.Should().Be("bing");
        hops[1].ToProvider.Should().Be("browser-serp");
    }

    [Fact]
    public async Task DoesNotReportFailoverWhenPrimarySucceeds()
    {
        var hops = new List<SearchFailover>();
        var router = new SearchRouter(
            new ISearchProvider[]
            {
                new FakeProvider("serpapi", _ => Hits("serpapi", "https://a")),
                new FakeProvider("browser-serp", _ => Hits("browser-serp", "https://b"))
            },
            hops.Add);

        await router.SearchAsync(Web);

        hops.Should().BeEmpty();
    }

    [Fact]
    public async Task SkipsProvidersThatDoNotSupportKind()
    {
        // browser-serp is Web-only; a Shopping query must skip it entirely and use serpapi.
        var browser = new FakeProvider("browser-serp", _ => Hits("browser-serp", "https://b"),
            supports: k => k == SearchKind.Web);
        var router = new SearchRouter(new FakeProvider("serpapi", _ => Hits("serpapi", "https://a")), browser);

        var shopping = await router.SearchAsync(new SearchQuery { Query = "q", Kind = SearchKind.Shopping });

        shopping.Provider.Should().Be("serpapi");
        browser.Calls.Should().Be(0);
    }

    /// <summary>A provider that blocks for <c>delay</c> (honouring cancellation) before answering,
    /// so tests can exercise a slow primary being overtaken by a hedged fallback.</summary>
    private sealed class DelayedProvider : ISearchProvider
    {
        private readonly TimeSpan _delay;
        private readonly SearchResults _result;

        public DelayedProvider(string name, TimeSpan delay, SearchResults result)
            => (Name, _delay, _result) = (name, delay, result);

        public string Name { get; }
        public bool Started { get; private set; }
        public bool CompletedNormally { get; private set; }
        public bool Supports(SearchKind kind) => true;

        public async Task<SearchResults> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
        {
            Started = true;
            await Task.Delay(_delay, cancellationToken).ConfigureAwait(false);
            CompletedNormally = true;
            return _result;
        }
    }

    [Fact]
    public async Task HedgesToFallbackWhenPrimaryLags_WithoutWaitingItOut()
    {
        // The primary would take 10s; the hedge is 20ms. The router must spin up the fallback the
        // moment the primary lags past the hedge and return it — never blocking on the slow primary.
        var slowPrimary = new DelayedProvider("serpapi", TimeSpan.FromSeconds(10), Hits("serpapi", "https://a"));
        var fastFallback = new FakeProvider("bing", _ => Hits("bing", "https://b"));
        var router = new SearchRouter(
            new ISearchProvider[] { slowPrimary, fastFallback },
            onFailover: null, hedgeDelay: TimeSpan.FromMilliseconds(20));

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var results = await router.SearchAsync(Web);
        stopwatch.Stop();

        results.Provider.Should().Be("bing", "the fallback answered first once the primary lagged past the hedge");
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "the 10s primary must not be waited out");
        slowPrimary.Started.Should().BeTrue();
        slowPrimary.CompletedNormally.Should().BeFalse("the overtaken primary is cancelled once a winner is found");
    }

    [Fact]
    public async Task ReportsSlowHopWhenPrimaryLags()
    {
        var hops = new List<SearchFailover>();
        var slowPrimary = new DelayedProvider("serpapi", TimeSpan.FromSeconds(10), Hits("serpapi", "https://a"));
        var router = new SearchRouter(
            new ISearchProvider[] { slowPrimary, new FakeProvider("bing", _ => Hits("bing", "https://b")) },
            hops.Add, hedgeDelay: TimeSpan.FromMilliseconds(20));

        await router.SearchAsync(Web);

        hops.Should().ContainSingle();
        hops[0].FromProvider.Should().Be("serpapi");
        hops[0].ToProvider.Should().Be("bing");
        hops[0].Reason.Should().Contain("slow");
    }

    [Fact]
    public async Task ZeroHedge_RacesFallbackImmediately()
    {
        // Hedge delay 0 collapses to full-parallel racing: the fallback is started at once and wins
        // while the slow primary is still running.
        var slowPrimary = new DelayedProvider("serpapi", TimeSpan.FromSeconds(10), Hits("serpapi", "https://a"));
        var fastFallback = new FakeProvider("bing", _ => Hits("bing", "https://b"));
        var router = new SearchRouter(
            new ISearchProvider[] { slowPrimary, fastFallback },
            onFailover: null, hedgeDelay: TimeSpan.Zero);

        var results = await router.SearchAsync(Web);

        results.Provider.Should().Be("bing");
        fastFallback.Calls.Should().Be(1);
    }

    [Fact]
    public void Supports_TrueIfAnyMemberSupports()
    {
        var router = new SearchRouter(
            new FakeProvider("browser-serp", _ => Empty("browser-serp"), supports: k => k == SearchKind.Web));

        router.Supports(SearchKind.Web).Should().BeTrue();
        router.Supports(SearchKind.Shopping).Should().BeFalse();
    }

    [Fact]
    public async Task ReturnsEmptyWhenAllProvidersFail()
    {
        var router = new SearchRouter(
            Throws("serpapi"), new FakeProvider("browser-serp", _ => Empty("browser-serp")));

        var results = await router.SearchAsync(Web);

        results.Results.Should().BeEmpty();
    }

    [Fact]
    public void RejectsEmptyChain()
    {
        var act = () => new SearchRouter();
        act.Should().Throw<ArgumentException>();
    }
}
