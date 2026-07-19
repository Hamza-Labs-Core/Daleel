using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Daleel.Web.Pipeline.Inventory;

/// <summary>
/// WooCommerce's public Store API (<c>/wp-json/wc/store/v1/products</c>) — the second free-class
/// catalogue source (structured JSON, no rendering, no LLM). Present unauthenticated on most Woo
/// installs; QA proof: leaders.jo serves it.
/// </summary>
public sealed class WooCommerceCatalogClient : IStoreCatalogClient, IDisposable
{
    /// <summary>Store API maximum page size.</summary>
    public const int PageSize = 100;

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Daleel.Web.Services.IProviderApi? _providers;

    public WooCommerceCatalogClient(HttpClient? http = null, Daleel.Web.Services.IProviderApi? providers = null)
    {
        _ownsHttp = http is null;
        _http = http ?? Daleel.Search.Http.SharedHttpHandler.CreateClient();
        _providers = providers;
        if (_http.Timeout == default || _http.Timeout == TimeSpan.FromSeconds(100))
        {
            _http.Timeout = TimeSpan.FromSeconds(30);
        }
    }

    public async Task<(IReadOnlyList<InventoryListing> Listings, string RawPayload)?> GetPageAsync(
        string domain, int page, CancellationToken ct = default)
    {
        var url = $"https://{domain}/wp-json/wc/store/v1/products?per_page={PageSize}&page={page}";

        // Direct first, then the provider chain — same bot-wall reality as Shopify (the invariant).
        var json = await FetchAsync(url, ct).ConfigureAwait(false);
        var listings = json is null ? null : Parse(json, domain);
        if (listings is null && _providers is not null)
        {
            json = await FetchViaProvidersAsync(url, ct).ConfigureAwait(false);
            listings = json is null ? null : Parse(json, domain);
        }

        return listings is null || json is null ? null : (listings, json);
    }

    private async Task<string?> FetchAsync(string url, CancellationToken ct)
    {
        try
        {
            using var res = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (res.IsSuccessStatusCode)
            {
                return await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }

            // Unlike Shopify (empty array past the end), the Store API 400s on an out-of-range page
            // (rest_invalid_param). That is END-OF-CATALOGUE, not an outage — surfacing it as
            // unreadable killed 200 units per big-store sync on QA.
            if (res.StatusCode == System.Net.HttpStatusCode.BadRequest)
            {
                var body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (body.Contains("rest_invalid", StringComparison.OrdinalIgnoreCase) ||
                    body.Contains("rest_", StringComparison.OrdinalIgnoreCase))
                {
                    return "[]";
                }
            }

            return null;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return null;
        }
    }

    private async Task<string?> FetchViaProvidersAsync(string url, CancellationToken ct)
    {
        try
        {
            var page = await _providers!.ScrapePageAsync(url, Daleel.Search.Abstractions.ScrapeFormat.Html, ct)
                .ConfigureAwait(false);
            if (page?.Content is not { Length: > 0 } content)
            {
                return null;
            }

            // A browser renders the JSON array inside an HTML shell — cut [first..last] bracket.
            var start = content.IndexOf('[');
            var end = content.LastIndexOf(']');
            return start >= 0 && end > start ? content[start..(end + 1)] : null;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return null;
        }
    }

    /// <summary>Null when the payload isn't a Store-API product array; empty when past the end.</summary>
    internal static IReadOnlyList<InventoryListing>? Parse(string json, string domain)
    {
        List<WooProduct>? products;
        try
        {
            products = JsonSerializer.Deserialize<List<WooProduct>>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }

        if (products is null)
        {
            return null;
        }

        var list = new List<InventoryListing>(products.Count);
        foreach (var p in products)
        {
            if (string.IsNullOrWhiteSpace(p.Name))
            {
                continue;
            }

            list.Add(new InventoryListing(
                Name: System.Net.WebUtility.HtmlDecode(p.Name).Trim(),
                Brand: p.Brands?.FirstOrDefault()?.Name,
                Sku: string.IsNullOrWhiteSpace(p.Sku) ? null : p.Sku.Trim(),
                Category: p.Categories?.FirstOrDefault()?.Name is { Length: > 0 } c
                    ? System.Net.WebUtility.HtmlDecode(c)
                    : null,
                Price: p.Prices?.Value,
                Available: p.IsInStock ?? true,
                Url: p.Permalink,
                ImageUrl: p.Images?.FirstOrDefault()?.Src));
        }

        return list;
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private sealed class WooProduct
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("sku")] public string? Sku { get; set; }
        [JsonPropertyName("permalink")] public string? Permalink { get; set; }
        [JsonPropertyName("is_in_stock")] public bool? IsInStock { get; set; }
        [JsonPropertyName("prices")] public WooPrices? Prices { get; set; }
        [JsonPropertyName("images")] public List<WooImage>? Images { get; set; }
        [JsonPropertyName("categories")] public List<WooTerm>? Categories { get; set; }
        [JsonPropertyName("brands")] public List<WooTerm>? Brands { get; set; }
    }

    private sealed class WooPrices
    {
        // Store-API prices are MINOR-unit strings ("12990" with currency_minor_unit=2 → 129.90).
        [JsonPropertyName("price")] public string? Price { get; set; }
        [JsonPropertyName("currency_minor_unit")] public int? MinorUnit { get; set; }

        [JsonIgnore]
        public decimal? Value =>
            decimal.TryParse(Price, out var v)
                ? v / (decimal)Math.Pow(10, MinorUnit ?? 2)
                : null;
    }

    private sealed class WooImage
    {
        [JsonPropertyName("src")] public string? Src { get; set; }
    }

    private sealed class WooTerm
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
    }

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }
}

/// <summary>
/// Probes the free-class catalogue platforms in order (Shopify → WooCommerce) and memoizes which
/// one a domain speaks so later pages skip the probe. A domain speaking neither returns null and
/// the sync fails visibly (the LLM crawl mode for plain-HTML stores is the spec's next step).
/// </summary>
public sealed class CompositeCatalogClient : IStoreCatalogClient
{
    private readonly IReadOnlyList<IStoreCatalogClient> _clients;
    private readonly ConcurrentDictionary<string, int> _platformByDomain = new(StringComparer.OrdinalIgnoreCase);

    public CompositeCatalogClient(params IStoreCatalogClient[] clients) => _clients = clients;

    public async Task<(IReadOnlyList<InventoryListing> Listings, string RawPayload)?> GetPageAsync(
        string domain, int page, CancellationToken ct = default)
    {
        if (_platformByDomain.TryGetValue(domain, out var known))
        {
            return await _clients[known].GetPageAsync(domain, page, ct).ConfigureAwait(false);
        }

        for (var i = 0; i < _clients.Count; i++)
        {
            var result = await _clients[i].GetPageAsync(domain, page, ct).ConfigureAwait(false);
            if (result is not null)
            {
                _platformByDomain[domain] = i;
                return result;
            }
        }

        return null;
    }
}
