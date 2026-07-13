using System.Text.RegularExpressions;
using System.Web;
using Daleel.Search.Abstractions;

namespace Daleel.Search.Providers;

/// <summary>
/// A web-discovery fallback that needs NO search-vendor quota: it renders the DuckDuckGo HTML search
/// endpoint with the edge browser and parses the organic result links out of the rendered markdown.
/// </summary>
/// <remarks>
/// Its reason to exist is SerpAPI monthly-quota exhaustion: when the vendor is out, web discovery — the
/// seed for every store/brand crawl — would otherwise degrade to a silent empty.
///
/// It renders the <b>DuckDuckGo HTML endpoint</b> (<c>html.duckduckgo.com/html/</c>), NOT Bing, and
/// parses the markdown deterministically, NOT via AI-schema extraction. The previous version asked
/// Cloudflare's <c>/browser-rendering/json</c> AI extractor to pull organic results from a rendered Bing
/// SERP; verified live on QA (jobs 49/50) that returned 0 results on EVERY call while still billing —
/// a ~240k-char SERP is far past what schema extraction can parse. DuckDuckGo's HTML endpoint renders
/// to a few KB of clean markdown, serves relevant geo-targeted results to a headless datacenter browser
/// without a consent/captcha wall, and — crucially — wraps every organic result URL in a
/// <c>/l/?uddg=&lt;real-url&gt;</c> redirect, so organic results are trivially separable from ads
/// (<c>/y.js</c>) and internal nav links. Discovery only (<see cref="SearchKind.Web"/>/<see cref="SearchKind.News"/>);
/// Shopping and Maps have their own dedicated providers. The engine is a URL template so an operator
/// can retarget it (<c>SEARCH_SERP_ENGINE</c>) without a redeploy.
/// </remarks>
public sealed class BrowserSearchProvider : ISearchProvider
{
    /// <summary>DuckDuckGo HTML search. Placeholders: {q} the escaped query, {cc} country, {lang} language.</summary>
    public const string DefaultEngine = "https://html.duckduckgo.com/html/?q={q}";

    /// <summary>DDG wraps every organic result URL in <c>/l/?uddg=&lt;url-encoded real url&gt;</c>.</summary>
    private static readonly Regex ResultLink = new(
        @"\[(?<title>[^\]]*)\]\(\s*(?<href>[^)\s]*duckduckgo\.com/l/\?[^)\s]*uddg=[^)\s]+)\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IScrapeProvider _scraper;
    private readonly string _engine;

    public string Name => "browser-serp";

    /// <param name="scraper">The rendering scraper (Cloudflare edge browser, or any IScrapeProvider).</param>
    /// <param name="engine">SERP URL template; null reads <c>SEARCH_SERP_ENGINE</c>, then falls to DuckDuckGo.</param>
    public BrowserSearchProvider(IScrapeProvider scraper, string? engine = null)
    {
        _scraper = scraper ?? throw new ArgumentNullException(nameof(scraper));
        _engine = string.IsNullOrWhiteSpace(engine)
            ? (Environment.GetEnvironmentVariable("SEARCH_SERP_ENGINE") ?? DefaultEngine)
            : engine;
    }

    public bool Supports(SearchKind kind) => kind is SearchKind.Web or SearchKind.News;

    public async Task<SearchResults> SearchAsync(
        SearchQuery query, CancellationToken cancellationToken = default)
    {
        if (!Supports(query.Kind))
        {
            return SearchResults.Empty(Name, query.Query, query.Kind);
        }

        var url = BuildUrl(query);
        var page = await _scraper.ScrapeAsync(url, ScrapeFormat.Markdown, cancellationToken).ConfigureAwait(false);

        var results = ParseResults(page?.Content, query)
            .Take(Math.Max(1, query.MaxResults))
            .ToList();

        return new SearchResults
        {
            Provider = Name,
            Query = query.Query,
            Kind = query.Kind,
            Results = results
        };
    }

    private string BuildUrl(SearchQuery query) => _engine
        .Replace("{q}", Uri.EscapeDataString(query.Query))
        .Replace("{cc}", Uri.EscapeDataString(query.CountryCode ?? string.Empty))
        .Replace("{lang}", Uri.EscapeDataString(query.LanguageCode ?? string.Empty));

    private IEnumerable<SearchResult> ParseResults(string? markdown, SearchQuery query)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var position = 0;
        foreach (Match m in ResultLink.Matches(markdown))
        {
            var real = UnwrapUddg(m.Groups["href"].Value);
            if (real is null || !seen.Add(real))
            {
                continue; // undecodable, or a duplicate result already emitted
            }

            position++;
            yield return new SearchResult
            {
                Title = m.Groups["title"].Value.Trim(),
                Url = real,
                Snippet = string.Empty,
                Source = Name,
                Kind = query.Kind,
                Position = position
            };
        }
    }

    /// <summary>Pulls the real destination out of a DDG <c>/l/?...uddg=&lt;encoded&gt;...</c> redirect.</summary>
    private static string? UnwrapUddg(string href)
    {
        var q = href.IndexOf('?');
        if (q < 0)
        {
            return null;
        }

        var uddg = HttpUtility.ParseQueryString(href[(q + 1)..]).Get("uddg");
        if (string.IsNullOrWhiteSpace(uddg))
        {
            return null;
        }

        var decoded = Uri.UnescapeDataString(uddg);
        return decoded.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? decoded : null;
    }
}
