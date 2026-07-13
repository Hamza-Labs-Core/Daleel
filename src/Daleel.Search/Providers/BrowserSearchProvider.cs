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
    /// <summary>
    /// DuckDuckGo's HTML endpoint — a no-JS, scraper-friendly SERP that renders to a few KB of clean
    /// markdown with organic results wrapped in <c>/l/?uddg=</c>. Bing serves Cloudflare's datacenter
    /// browser only a ~300-char consent/block stub, so DDG is the reliable engine (the earlier "0-char"
    /// DDG render was the browser-rendering token's 401, not DDG). Placeholders: {q} {cc} {lang}.
    /// </summary>
    public const string DefaultEngine = "https://html.duckduckgo.com/html/?q={q}";

    /// <summary>Every markdown link on the page; which ones are organic results is decided per-link below.</summary>
    private static readonly Regex MarkdownLink = new(
        @"\[(?<title>[^\]]*)\]\(\s*(?<href>[^)\s]+)\)",
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
        var content = page?.Content ?? string.Empty;

        var results = ParseResults(content, query)
            .Take(Math.Max(1, query.MaxResults))
            .ToList();

        return new SearchResults
        {
            Provider = Name,
            Query = query.Query,
            Kind = query.Kind,
            Results = results,
            // When empty, carry WHY to the timeline (SearchRouter surfaces an empty attempt's Diagnostic):
            // the render char count AND the scraper's own error/success — so we can tell a 0-char render
            // caused by a failing/exhausted scraper apart from a rendered page the parser missed.
            Diagnostic = results.Count > 0
                ? null
                : $"browser-serp: {Name} rendered {content.Length} chars from {Host(url)} " +
                  $"(scrape ok={page?.Success}, err={page?.Error ?? "none"})"
        };
    }

    private static string Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;

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
        foreach (Match m in MarkdownLink.Matches(markdown))
        {
            var real = ResolveResultUrl(m.Groups["href"].Value);
            if (real is null || !seen.Add(real))
            {
                continue; // not an organic result, or a duplicate already emitted
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

    /// <summary>Search-engine hosts whose own links (nav, ads, redirects we couldn't decode) are never results.</summary>
    private static readonly string[] EngineHosts = { "bing.com", "microsoft.com", "msn.com", "duckduckgo.com", "google.com" };

    /// <summary>
    /// The real destination of a markdown link if it is an organic result, else null. Handles the SERP
    /// redirect wrappers (Bing <c>/ck/a?...u=&lt;base64url&gt;</c>, DDG <c>/l/?uddg=&lt;encoded&gt;</c>) AND
    /// links the markdown converter already resolved to the final URL; search-engine chrome/ads are dropped.
    /// </summary>
    private static string? ResolveResultUrl(string href)
    {
        var link = href.StartsWith("//", StringComparison.Ordinal) ? "https:" + href : href;
        if (!Uri.TryCreate(link, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https"))
        {
            return null;
        }

        var host = u.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? u.Host[4..] : u.Host;
        var query = HttpUtility.ParseQueryString(u.Query);

        // Bing wraps organic results in /ck/a?...&u=a1<base64url(real url)>.
        if (host.EndsWith("bing.com", StringComparison.OrdinalIgnoreCase) &&
            u.AbsolutePath.StartsWith("/ck/", StringComparison.OrdinalIgnoreCase))
        {
            return DecodeBingU(query.Get("u"));
        }

        // DuckDuckGo wraps organic results in /l/?uddg=<encoded real url>.
        if (host.EndsWith("duckduckgo.com", StringComparison.OrdinalIgnoreCase) &&
            u.AbsolutePath.StartsWith("/l/", StringComparison.OrdinalIgnoreCase))
        {
            var uddg = query.Get("uddg");
            var decoded = string.IsNullOrWhiteSpace(uddg) ? null : Uri.UnescapeDataString(uddg);
            return Http(decoded);
        }

        // Otherwise: a direct/resolved external result — keep it unless it's a search-engine host.
        return EngineHosts.Any(e => host.Equals(e, StringComparison.OrdinalIgnoreCase) ||
                                    host.EndsWith("." + e, StringComparison.OrdinalIgnoreCase))
            ? null
            : link;
    }

    /// <summary>Bing's <c>u</c> param is <c>"a1" + base64url(real url)</c>.</summary>
    private static string? DecodeBingU(string? u)
    {
        if (string.IsNullOrWhiteSpace(u) || !u.StartsWith("a1", StringComparison.Ordinal))
        {
            return null;
        }

        var b64 = u[2..].Replace('-', '+').Replace('_', '/');
        b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
        try
        {
            return Http(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64)));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string? Http(string? url) =>
        url is not null && url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : null;
}
