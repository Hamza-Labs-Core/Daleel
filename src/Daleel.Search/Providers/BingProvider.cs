using System.Text.Json;
using Daleel.Search.Abstractions;
using Daleel.Search.Http;

namespace Daleel.Search.Providers;

/// <summary>
/// Web/news search via the Bing Web Search API. Geo/language targeting uses the
/// <c>mkt</c> market parameter (e.g. "ar-JO"). Auth is the
/// <c>Ocp-Apim-Subscription-Key</c> header.
/// </summary>
public sealed class BingProvider : HttpProviderBase, ISearchProvider
{
    public const string DefaultBaseUrl = "https://api.bing.microsoft.com";

    private readonly string _apiKey;
    public string Name => "bing";
    protected override string ProviderName => Name;

    /// <param name="perAttemptTimeout">
    /// Hard cap on each Bing request attempt. As the discovery fallback, an unbounded Bing call would
    /// just relocate the "slow" problem — a stall here rode <see cref="HttpClient"/>'s 100s default
    /// across every retry before the router reached the browser-SERP last resort. Null resolves the
    /// default (env <c>BING_TIMEOUT_SECONDS</c>, else 20s, clamped 1–30s).
    /// </param>
    public BingProvider(
        string? apiKey = null,
        HttpClient? httpClient = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeSpan? perAttemptTimeout = null)
        : base(ConfigureClient(httpClient), maxRetries: 2, delay,
            perAttemptTimeout ?? ResolveAttemptTimeout("BING_TIMEOUT_SECONDS", 20))
    {
        _apiKey = apiKey
                  ?? Environment.GetEnvironmentVariable("BING_SEARCH_KEY")
                  ?? throw new ProviderException("BING_SEARCH_KEY is not set.");
    }

    private static HttpClient ConfigureClient(HttpClient? client)
    {
        client ??= SharedHttpHandler.CreateClient();
        client.BaseAddress ??= new Uri(DefaultBaseUrl);
        return client;
    }

    public bool Supports(SearchKind kind) => kind is SearchKind.Web or SearchKind.News;

    public async Task<SearchResults> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        var path = query.Kind == SearchKind.News ? "/v7.0/news/search" : "/v7.0/search";
        var market = ToMarket(query.LanguageCode, query.CountryCode);

        var url = $"{path}?q={Uri.EscapeDataString(query.Query)}&count={query.MaxResults}" +
                  (market is null ? string.Empty : $"&mkt={Uri.EscapeDataString(market)}");

        using var doc = await SendJsonAsync(
            () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Add("Ocp-Apim-Subscription-Key", _apiKey);
                return req;
            },
            cancellationToken).ConfigureAwait(false);

        var results = ParseWeb(doc.RootElement, query.MaxResults);
        return new SearchResults { Provider = Name, Query = query.Query, Kind = query.Kind, Results = results };
    }

    private List<SearchResult> ParseWeb(JsonElement root, int max)
    {
        var list = new List<SearchResult>();

        // News shape: { value: [ { name, url, description } ] }
        if (root.TryGetProperty("value", out var newsArr) && newsArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in newsArr.EnumerateArray())
            {
                list.Add(MapItem(item, "name", "url", "description"));
                if (list.Count >= max) break;
            }
            if (list.Count > 0) return list;
        }

        // Web shape: { webPages: { value: [ { name, url, snippet } ] } }
        if (root.TryGetProperty("webPages", out var web) &&
            web.TryGetProperty("value", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                list.Add(MapItem(item, "name", "url", "snippet"));
                if (list.Count >= max) break;
            }
        }

        return list;
    }

    private SearchResult MapItem(JsonElement item, string titleKey, string urlKey, string snippetKey) => new()
    {
        Title = Str(item, titleKey),
        Url = StrOrNull(item, urlKey),
        Snippet = Str(item, snippetKey),
        Source = Name,
        Kind = SearchKind.Web
    };

    /// <summary>Builds a Bing market code like "ar-JO" from language + country.</summary>
    private static string? ToMarket(string? language, string? country)
    {
        if (string.IsNullOrWhiteSpace(country))
        {
            return null;
        }

        var lang = string.IsNullOrWhiteSpace(language) ? "en" : language.Split('-')[0];
        return $"{lang}-{country.ToUpperInvariant()}";
    }

    private static string Str(JsonElement e, string prop) => StrOrNull(e, prop) ?? string.Empty;
    private static string? StrOrNull(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
