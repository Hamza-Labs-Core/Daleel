using Daleel.Search.Abstractions;
using Daleel.Search.Providers;
using FluentAssertions;
using Xunit;

namespace Daleel.Search.Tests;

// browser-serp is the discovery fallback for when SerpAPI's monthly quota is exhausted. The original
// implementation asked Cloudflare's /browser-rendering/json AI extractor to pull organic results out
// of a rendered Bing SERP — verified live (QA jobs 49/50) to return 0 results on EVERY call while
// still billing, because AI-schema extraction can't parse a 240k-char SERP. This version renders the
// DuckDuckGo HTML endpoint as clean markdown and parses the organic result links deterministically
// (DDG wraps every organic URL in /l/?uddg=<real-url>, so they're trivially separable from ads/nav).
public class BrowserSearchProviderTests
{
    private sealed class FakeScraper : IScrapeProvider
    {
        private readonly string _markdown;
        public FakeScraper(string markdown) => _markdown = markdown;
        public string Name => "fake-scraper";
        public string? LastUrl { get; private set; }
        public ScrapeFormat? LastFormat { get; private set; }
        public Task<ScrapedPage> ScrapeAsync(string url, ScrapeFormat format = ScrapeFormat.Markdown, CancellationToken ct = default)
        {
            LastUrl = url; LastFormat = format;
            return Task.FromResult(new ScrapedPage { Url = url, Content = _markdown, Provider = Name });
        }
    }

    // A realistic slice of the DDG HTML endpoint rendered to markdown: organic results wrapped in
    // /l/?uddg=, one ad (/y.js), and internal nav links back to duckduckgo.com.
    private const string DdgMarkdown = """
        [Web](https://duckduckgo.com/?q=coffee+grinder+amman)
        [Sponsored: Buy Grinders](https://duckduckgo.com/y.js?ad_provider=bingv7aa&u3=https%3A%2F%2Fads.example)
        [Coffee grinder - Amman Hardware stores](https://duckduckgo.com/l/?uddg=https%3A%2F%2Fwww.ammanhardware.com%2Fen%2Fcoffee-grinder&rut=aaa)
        [Coffee Grinders - Ammancart](https://duckduckgo.com/l/?uddg=https%3A%2F%2Fwww.ammancart.com%2Far%2Fcoffee-grinders&rut=bbb)
        [Coffee Grinders for Sale - OpenSooq](//duckduckgo.com/l/?uddg=https%3A%2F%2Fjo.opensooq.com%2Fen%2Famman&rut=ccc)
        [Settings](https://duckduckgo.com/settings)
        """;

    private static BrowserSearchProvider Build(string markdown) => new(new FakeScraper(markdown));

    // Bing organic results, rendered to markdown, wrap the real URL in /ck/a?...&u=a1<base64url>.
    private static string BingCkA(string title, string realUrl)
    {
        var b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(realUrl))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        return $"[{title}](https://www.bing.com/ck/a?!&&p=abc&u=a1{b64}&ntb=1)";
    }

    [Fact]
    public async Task Renders_bing_as_markdown_with_the_escaped_query()
    {
        var scraper = new FakeScraper(DdgMarkdown);
        var provider = new BrowserSearchProvider(scraper);

        await provider.SearchAsync(new SearchQuery { Query = "coffee & tea", Kind = SearchKind.Web });

        scraper.LastFormat.Should().Be(ScrapeFormat.Markdown);
        scraper.LastUrl.Should().StartWith("https://www.bing.com/search?q=");
        scraper.LastUrl.Should().Contain("coffee%20%26%20tea");
    }

    [Fact]
    public async Task Decodes_bing_ck_a_redirects_and_skips_bing_chrome()
    {
        var markdown = string.Join("\n",
            "[Images](https://www.bing.com/images/search?q=x)",
            BingCkA("Coffee Grinders - Ammancart", "https://www.ammancart.com/ar/coffee-grinders"),
            BingCkA("Leaders Center", "https://leaders.jo/en/coffee"));
        var results = (await Build(markdown).SearchAsync(
            new SearchQuery { Query = "x", Kind = SearchKind.Web })).Results;

        results.Select(r => r.Url).Should().Equal(
            "https://www.ammancart.com/ar/coffee-grinders", "https://leaders.jo/en/coffee");
        results[0].Title.Should().Be("Coffee Grinders - Ammancart");
    }

    [Fact]
    public async Task Extracts_organic_results_unwrapping_the_ddg_redirect()
    {
        var results = (await Build(DdgMarkdown).SearchAsync(
            new SearchQuery { Query = "coffee grinder amman", Kind = SearchKind.Web })).Results;

        results.Should().HaveCount(3);
        results[0].Url.Should().Be("https://www.ammanhardware.com/en/coffee-grinder");
        results[0].Title.Should().Be("Coffee grinder - Amman Hardware stores");
        results[0].Source.Should().Be("browser-serp");
        results[0].Position.Should().Be(1);
        results.Select(r => r.Url).Should().Contain("https://jo.opensooq.com/en/amman", "protocol-relative /l/ links count too");
    }

    [Fact]
    public async Task Also_handles_links_the_markdown_converter_already_resolved()
    {
        // CF's /markdown sometimes follows DDG's /l/ redirect and emits the FINAL url instead of the
        // wrapper — those direct external links must count as results too (search-engine hosts don't).
        const string resolved = """
            [Web](https://duckduckgo.com/?q=x)
            [Coffee grinder - Amman Hardware](https://www.ammanhardware.com/en/coffee-grinder)
            [Bunni Coffee](https://bunni.coffee/collections/grinders)
            """;
        var results = (await Build(resolved).SearchAsync(
            new SearchQuery { Query = "x", Kind = SearchKind.Web })).Results;

        results.Select(r => r.Url).Should().Equal(
            "https://www.ammanhardware.com/en/coffee-grinder", "https://bunni.coffee/collections/grinders");
    }

    [Fact]
    public async Task Empty_results_carry_a_render_diagnostic_for_the_timeline()
    {
        var r = await Build("a challenge page with no links, 42 chars here").SearchAsync(
            new SearchQuery { Query = "x", Kind = SearchKind.Web });

        r.Results.Should().BeEmpty();
        r.Diagnostic.Should().Contain("rendered").And.Contain("chars").And.Contain("bing.com",
            "the diagnostic must reveal whether the engine rendered anything at all");
    }

    [Fact]
    public async Task Skips_ads_and_internal_navigation_links()
    {
        var results = (await Build(DdgMarkdown).SearchAsync(
            new SearchQuery { Query = "x", Kind = SearchKind.Web })).Results;

        results.Select(r => r.Url!).Should().OnlyContain(u => !u.Contains("duckduckgo.com") && !u.Contains("ads.example"));
    }

    [Fact]
    public async Task Dedupes_repeated_result_urls_and_respects_max_results()
    {
        const string dup = """
            [A](https://duckduckgo.com/l/?uddg=https%3A%2F%2Fa.jo%2Fp)
            [A again](https://duckduckgo.com/l/?uddg=https%3A%2F%2Fa.jo%2Fp)
            [B](https://duckduckgo.com/l/?uddg=https%3A%2F%2Fb.jo%2Fp)
            [C](https://duckduckgo.com/l/?uddg=https%3A%2F%2Fc.jo%2Fp)
            """;
        var results = (await Build(dup).SearchAsync(
            new SearchQuery { Query = "x", Kind = SearchKind.Web, MaxResults = 2 })).Results;

        results.Should().HaveCount(2, "deduped to a.jo/b.jo/c.jo then capped at MaxResults");
        results.Select(r => r.Url).Should().Equal("https://a.jo/p", "https://b.jo/p");
    }

    [Fact]
    public async Task Empty_or_resultless_markdown_yields_no_results_and_does_not_throw()
    {
        (await Build("").SearchAsync(new SearchQuery { Query = "x", Kind = SearchKind.Web }))
            .Results.Should().BeEmpty();
        (await Build("just some prose with no result links").SearchAsync(
            new SearchQuery { Query = "x", Kind = SearchKind.Web })).Results.Should().BeEmpty();
    }

    [Fact]
    public async Task Only_supports_web_and_news_discovery()
    {
        var p = Build(DdgMarkdown);
        p.Supports(SearchKind.Web).Should().BeTrue();
        p.Supports(SearchKind.News).Should().BeTrue();
        p.Supports(SearchKind.Shopping).Should().BeFalse();
        (await p.SearchAsync(new SearchQuery { Query = "x", Kind = SearchKind.Shopping })).Results.Should().BeEmpty();
    }
}
