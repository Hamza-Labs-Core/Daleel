using System.Text.Json;
using Daleel.Search.Abstractions;

namespace Daleel.Search.Providers;

/// <summary>
/// A web-discovery fallback that needs NO search-vendor quota: it renders a search-engine results
/// page with the Cloudflare edge browser and pulls the organic result links out with Workers-AI
/// structured extraction (the same <c>/browser-rendering/json</c> path the store extractor uses).
/// </summary>
/// <remarks>
/// Its reason to exist is SerpAPI monthly-quota exhaustion: when the vendor returns non-2xx, web
/// discovery — the seed for every store/brand crawl — would otherwise degrade to a silent empty.
/// This scrapes a SERP instead. Discovery only (<see cref="SearchKind.Web"/>/<see cref="SearchKind.News"/>);
/// Shopping and Maps have their own dedicated providers. The engine is a URL template so an operator
/// can retarget it (<c>SEARCH_SERP_ENGINE</c>) without a redeploy; it defaults to Bing, which serves
/// results to a headless browser without the consent/captcha wall Google throws at datacenter IPs.
/// </remarks>
public sealed class BrowserSearchProvider : ISearchProvider
{
    /// <summary>Bing web search with geo/language targeting. Placeholders: {q} {cc} {lang}.</summary>
    public const string DefaultEngine = "https://www.bing.com/search?q={q}&cc={cc}&setlang={lang}";

    private readonly IExtractProvider _browser;
    private readonly string _engine;

    public string Name => "browser-serp";

    /// <param name="browser">The rendering extractor (Cloudflare edge browser, or any IExtractProvider).</param>
    /// <param name="engine">SERP URL template; null reads <c>SEARCH_SERP_ENGINE</c>, then falls to Bing.</param>
    public BrowserSearchProvider(IExtractProvider browser, string? engine = null)
    {
        _browser = browser ?? throw new ArgumentNullException(nameof(browser));
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
        var extracted = await _browser.ExtractAsync(url, SerpSchema, cancellationToken).ConfigureAwait(false);

        var results = ParseResults(extracted, query)
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

    /// <summary>The schema handed to Workers-AI extraction: the SERP's organic result list.</summary>
    private static readonly object SerpSchema = new
    {
        type = "object",
        properties = new
        {
            results = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        title = new { type = "string" },
                        url = new { type = "string" },
                        snippet = new { type = "string" }
                    }
                }
            }
        }
    };

    /// <summary>Wrapper keys the extraction may nest the result array under (tolerant of model drift).</summary>
    private static readonly string[] ArrayKeys = { "results", "items", "organic_results", "links" };

    private IEnumerable<SearchResult> ParseResults(JsonElement root, SearchQuery query)
    {
        var array = ResolveArray(root);
        if (array is null)
        {
            yield break;
        }

        var position = 0;
        foreach (var item in array.Value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var url = Str(item, "url");
            var title = Str(item, "title");

            // A SERP row with neither a link nor a title is noise (ad slot, "people also ask", etc.).
            if (string.IsNullOrWhiteSpace(url) && string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            position++;
            yield return new SearchResult
            {
                Title = title ?? string.Empty,
                Url = string.IsNullOrWhiteSpace(url) ? null : url,
                Snippet = Str(item, "snippet") ?? string.Empty,
                Source = Name,
                Kind = query.Kind,
                Position = position
            };
        }
    }

    private static JsonElement? ResolveArray(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            return root;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var key in ArrayKeys)
        {
            if (root.TryGetProperty(key, out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                return arr;
            }
        }

        return null;
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
