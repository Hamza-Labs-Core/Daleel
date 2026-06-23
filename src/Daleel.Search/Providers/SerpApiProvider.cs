using System.Text.Json;
using Daleel.Core.Pricing;
using Daleel.Search.Abstractions;
using Daleel.Search.Http;

namespace Daleel.Search.Providers;

/// <summary>
/// Primary search provider, backed by <see href="https://serpapi.com">SerpAPI</see>.
/// One API key covers Google Web, Shopping, and Maps; geo-targeting uses <c>gl</c>
/// (country) and <c>hl</c> (language).
/// </summary>
/// <remarks>
/// SerpAPI returns a single JSON object whose result arrays differ per engine
/// (<c>organic_results</c>, <c>shopping_results</c>, <c>local_results</c>). We map each
/// engine's shape into the common <see cref="SearchResult"/> so callers stay
/// engine-agnostic.
/// </remarks>
public sealed class SerpApiProvider : HttpProviderBase, ISearchProvider
{
    public const string DefaultBaseUrl = "https://serpapi.com";

    private readonly string _apiKey;
    public string Name => "serpapi";
    protected override string ProviderName => Name;

    public SerpApiProvider(
        string? apiKey = null,
        HttpClient? httpClient = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
        : base(ConfigureClient(httpClient), maxRetries: 2, delay)
    {
        _apiKey = apiKey
                  ?? Environment.GetEnvironmentVariable("SERPAPI_KEY")
                  ?? throw new ProviderException("SERPAPI_KEY is not set.");
    }

    private static HttpClient ConfigureClient(HttpClient? client)
    {
        client ??= SharedHttpHandler.CreateClient();
        client.BaseAddress ??= new Uri(DefaultBaseUrl);
        return client;
    }

    public bool Supports(SearchKind kind) =>
        kind is SearchKind.Web or SearchKind.Shopping or SearchKind.Maps or SearchKind.News;

    public async Task<SearchResults> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        var engine = query.Kind switch
        {
            SearchKind.Shopping => "google_shopping",
            SearchKind.Maps => "google_maps",
            SearchKind.News => "google_news",
            _ => "google"
        };

        var url = BuildUrl(engine, query);

        using var doc = await SendJsonAsync(
            () => new HttpRequestMessage(HttpMethod.Get, url),
            cancellationToken).ConfigureAwait(false);

        var results = query.Kind switch
        {
            SearchKind.Shopping => ParseShopping(doc.RootElement, query.MaxResults),
            SearchKind.Maps => ParseLocal(doc.RootElement, query.MaxResults),
            _ => ParseOrganic(doc.RootElement, query.MaxResults)
        };

        return new SearchResults
        {
            Provider = Name,
            Query = query.Query,
            Kind = query.Kind,
            Results = results
        };
    }

    private string BuildUrl(string engine, SearchQuery query)
    {
        var sb = new System.Text.StringBuilder("/search.json?");
        sb.Append("engine=").Append(Uri.EscapeDataString(engine));
        sb.Append("&q=").Append(Uri.EscapeDataString(query.Query));
        sb.Append("&num=").Append(query.MaxResults);
        // Halal/safety: always request Google SafeSearch so adult/unsafe hits never reach us.
        sb.Append("&safe=active");
        if (!string.IsNullOrWhiteSpace(query.CountryCode))
        {
            sb.Append("&gl=").Append(Uri.EscapeDataString(query.CountryCode));
        }
        if (!string.IsNullOrWhiteSpace(query.LanguageCode))
        {
            sb.Append("&hl=").Append(Uri.EscapeDataString(query.LanguageCode));
        }
        if (!string.IsNullOrWhiteSpace(query.Location))
        {
            sb.Append("&location=").Append(Uri.EscapeDataString(query.Location));
        }
        sb.Append("&api_key=").Append(Uri.EscapeDataString(_apiKey));
        return sb.ToString();
    }

    private List<SearchResult> ParseOrganic(JsonElement root, int max)
    {
        var list = new List<SearchResult>();
        if (root.TryGetProperty("organic_results", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                list.Add(new SearchResult
                {
                    Title = Str(item, "title"),
                    Url = StrOrNull(item, "link"),
                    Snippet = Str(item, "snippet"),
                    Source = Name,
                    Kind = SearchKind.Web,
                    Position = IntOrNull(item, "position")
                });
                if (list.Count >= max) break;
            }
        }

        return list;
    }

    private List<SearchResult> ParseShopping(JsonElement root, int max)
    {
        var list = new List<SearchResult>();
        if (root.TryGetProperty("shopping_results", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                var priceText = StrOrNull(item, "price");
                Daleel.Core.Models.Money? price = null;
                if (priceText is not null && PriceParser.TryParse(priceText, out var m))
                {
                    price = m;
                }

                list.Add(new SearchResult
                {
                    Title = Str(item, "title"),
                    Url = StrOrNull(item, "link") ?? StrOrNull(item, "product_link"),
                    Snippet = StrOrNull(item, "snippet") ?? string.Empty,
                    Source = Name,
                    Kind = SearchKind.Shopping,
                    Price = price,
                    Seller = StrOrNull(item, "source") ?? StrOrNull(item, "store"),
                    Rating = DblOrNull(item, "rating"),
                    Position = IntOrNull(item, "position")
                });
                if (list.Count >= max) break;
            }
        }

        return list;
    }

    private List<SearchResult> ParseLocal(JsonElement root, int max)
    {
        var list = new List<SearchResult>();
        if (root.TryGetProperty("local_results", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                list.Add(new SearchResult
                {
                    Title = Str(item, "title"),
                    Url = StrOrNull(item, "website") ?? StrOrNull(item, "link"),
                    Snippet = StrOrNull(item, "address") ?? string.Empty,
                    Source = Name,
                    Kind = SearchKind.Maps,
                    Seller = Str(item, "title"),
                    Rating = DblOrNull(item, "rating"),
                    Position = IntOrNull(item, "position")
                });
                if (list.Count >= max) break;
            }
        }

        return list;
    }

    // ── Small JSON readers ────────────────────────────────────────────────────
    private static string Str(JsonElement e, string prop) => StrOrNull(e, prop) ?? string.Empty;

    private static string? StrOrNull(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? IntOrNull(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)
            ? n : null;

    private static double? DblOrNull(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)
            ? d : null;
}
