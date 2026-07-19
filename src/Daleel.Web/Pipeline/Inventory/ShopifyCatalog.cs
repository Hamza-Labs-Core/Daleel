using System.Text.Json;
using System.Text.Json.Serialization;

namespace Daleel.Web.Pipeline.Inventory;

/// <summary>One parsed catalogue listing, provider-neutral (Shopify today, HTML crawlers later).</summary>
public sealed record InventoryListing(
    string Name, string? Brand, string? Sku, string? Category,
    decimal? Price, bool Available, string? Url, string? ImageUrl);

/// <summary>
/// Fetches a monitored store's catalogue, page by page. Abstracted so tests stub it and so an
/// HTML-crawl implementation can slot in behind the same units later.
/// </summary>
public interface IStoreCatalogClient
{
    /// <summary>The listings on catalogue page <paramref name="page"/> (1-based) plus the RAW payload
    /// (for hash-skip), or null when the store exposes no machine-readable catalogue.</summary>
    Task<(IReadOnlyList<InventoryListing> Listings, string RawPayload)?> GetPageAsync(
        string domain, int page, CancellationToken ct = default);
}

/// <summary>
/// Shopify's public <c>/products.json</c> — the free path: structured JSON, no rendering, no LLM.
/// Non-Shopify stores return null from the probe and fall to the (later) crawl-based mode.
/// </summary>
public sealed class ShopifyCatalogClient : IStoreCatalogClient, IDisposable
{
    /// <summary>Shopify's maximum page size — fewest requests per full walk.</summary>
    public const int PageSize = 250;

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public ShopifyCatalogClient(HttpClient? http = null)
    {
        _ownsHttp = http is null;
        _http = http ?? Daleel.Search.Http.SharedHttpHandler.CreateClient();
        if (_http.Timeout == default || _http.Timeout == TimeSpan.FromSeconds(100))
        {
            _http.Timeout = TimeSpan.FromSeconds(30);
        }
    }

    public async Task<(IReadOnlyList<InventoryListing> Listings, string RawPayload)?> GetPageAsync(
        string domain, int page, CancellationToken ct = default)
    {
        var url = $"https://{domain}/products.json?limit={PageSize}&page={page}";
        string json;
        try
        {
            using var res = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                return null;
            }

            json = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return null;
        }

        var listings = Parse(json, domain);
        return listings is null ? null : (listings, json);
    }

    /// <summary>Null when the payload isn't a Shopify products document at all (a bot-wall HTML page,
    /// a storefront that isn't Shopify); an EMPTY list when it is one with no products (end of pages).</summary>
    internal static IReadOnlyList<InventoryListing>? Parse(string json, string domain)
    {
        ShopifyProducts? doc;
        try
        {
            doc = JsonSerializer.Deserialize<ShopifyProducts>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }

        if (doc?.Products is null)
        {
            return null;
        }

        var list = new List<InventoryListing>(doc.Products.Count);
        foreach (var p in doc.Products)
        {
            if (string.IsNullOrWhiteSpace(p.Title))
            {
                continue;
            }

            // The representative variant: the cheapest AVAILABLE one, else the cheapest — matching
            // how the store itself headlines "from X JD".
            var variants = p.Variants ?? new List<ShopifyVariant>();
            var repr = variants.Where(v => v.Available == true).OrderBy(v => v.PriceValue).FirstOrDefault()
                       ?? variants.OrderBy(v => v.PriceValue).FirstOrDefault();

            list.Add(new InventoryListing(
                Name: p.Title.Trim(),
                Brand: string.IsNullOrWhiteSpace(p.Vendor) ? null : p.Vendor.Trim(),
                Sku: string.IsNullOrWhiteSpace(repr?.Sku) ? null : repr!.Sku!.Trim(),
                Category: string.IsNullOrWhiteSpace(p.ProductType) ? null : p.ProductType.Trim(),
                Price: repr?.PriceValue,
                Available: variants.Any(v => v.Available == true),
                Url: string.IsNullOrWhiteSpace(p.Handle) ? null : $"https://{domain}/products/{p.Handle}",
                ImageUrl: p.Images?.FirstOrDefault()?.Src));
        }

        return list;
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private sealed class ShopifyProducts
    {
        [JsonPropertyName("products")] public List<ShopifyProduct>? Products { get; set; }
    }

    private sealed class ShopifyProduct
    {
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("handle")] public string? Handle { get; set; }
        [JsonPropertyName("vendor")] public string? Vendor { get; set; }
        [JsonPropertyName("product_type")] public string? ProductType { get; set; }
        [JsonPropertyName("variants")] public List<ShopifyVariant>? Variants { get; set; }
        [JsonPropertyName("images")] public List<ShopifyImage>? Images { get; set; }
    }

    private sealed class ShopifyVariant
    {
        [JsonPropertyName("sku")] public string? Sku { get; set; }
        [JsonPropertyName("price")] public string? Price { get; set; }
        [JsonPropertyName("available")] public bool? Available { get; set; }

        [JsonIgnore]
        public decimal PriceValue => decimal.TryParse(Price, out var v) ? v : decimal.MaxValue;
    }

    private sealed class ShopifyImage
    {
        [JsonPropertyName("src")] public string? Src { get; set; }
    }

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }
}
