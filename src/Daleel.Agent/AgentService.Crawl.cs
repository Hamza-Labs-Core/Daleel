using System.Text.Json;
using System.Text.Json.Serialization;
using Daleel.Core.Geo;
using Daleel.Core.Llm;
using Daleel.Core.Models;
using Daleel.Core.Pricing;
using Daleel.Pipeline.Extraction;

namespace Daleel.Agent;

/// <summary>
/// The LLM reasoning behind the three specialised site crawlers — STORE, BRAND, and PRODUCT DETAIL. Each
/// page type has fundamentally different navigation and extraction patterns, so each gets its own prompts:
/// a store is walked for priced listings (search / categories / product API), a brand site is walked for its
/// model catalogue (the products section, not the marketing homepage), and a product page is mined for its
/// full record (specs, all images, reviews, related items, seller). Every call is metered under the
/// <see cref="LlmCallSites.Crawl"/> call-site and best-effort by construction (a failed/unparseable reply
/// degrades to a safe default), so a crawl can never fault the search.
/// </summary>
/// <remarks>
/// These methods own only the <em>reasoning</em>; rendering is the caller's job via <see cref="ReadPageAsync"/>
/// (which routes through the metered scraper — Context.dev, then Cloudflare Browser Rendering — and
/// SSRF-guards every URL). Keeping the reasoning here means all three crawlers inherit the agent's metering,
/// per-call-site model routing, and unit-testability with a fake LLM.
/// </remarks>
public sealed partial class AgentService
{
    /// <summary>Homepage markdown is cropped to this before an assessment call — the nav/links that reveal a
    /// site's structure live near the top, and a whole rendered page is mostly product noise.</summary>
    private const int AssessmentMaxChars = 12_000;

    /// <summary>A listing/catalogue page is cropped to this before extraction (pagination fetches the rest).</summary>
    private const int ListingMaxChars = 40_000;

    /// <summary>Pagination controls live at the page edges; a bounded slice keeps the detect call cheap.</summary>
    private const int PaginationMaxChars = 6_000;

    /// <summary>A product detail page is cropped to this before the full-detail extraction call.</summary>
    private const int DetailMaxChars = 18_000;

    // ══ STORE crawler ════════════════════════════════════════════════════════════

    /// <summary>
    /// Reads an e-commerce store's homepage and asks the LLM for its platform and the ways into its product
    /// catalogue — a search-URL template, category/collection pages, structured product endpoints (Shopify
    /// <c>/products.json</c>), and a sitemap. Every URL is absolutized against <paramref name="storeUrl"/>.
    /// Returns an empty (no-entry-point) assessment on any failure.
    /// </summary>
    public async Task<StoreAssessment> AssessStoreAsync(
        string storeUrl, string markdown, string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(markdown) || !Uri.TryCreate(NormalizeUrl(storeUrl), UriKind.Absolute, out var origin))
        {
            return new StoreAssessment();
        }

        var content = Crop(CleanForExtraction(markdown), AssessmentMaxChars);
        try
        {
            var dto = await CrawlJsonAsync<StoreAssessmentDto>(
                StoreAssessSystem, StoreAssessPrompt(storeUrl, query, content), cancellationToken);
            return dto is null ? new StoreAssessment() : MapStoreAssessment(dto, origin);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log($"store assessment failed for '{storeUrl}': {ex.Message}");
            return new StoreAssessment();
        }
    }

    /// <summary>
    /// Parses one STORE listing page: extracts its product cards with a transaction focus (price, currency,
    /// stock, SKU, product URL, image) and detects the pagination signals. Best-effort — extraction and
    /// pagination fail independently and degrade to "no products" / "no next page".
    /// </summary>
    public async Task<ListingPageResult> ExtractStoreListingAsync(
        string markdown, string pageUrl, string query, GeoProfile geo, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return new ListingPageResult();
        }

        var origin = OriginOf(pageUrl);
        var content = Crop(CleanForExtraction(markdown), ListingMaxChars);
        var products = await ExtractCrawlProductsAsync(
            StoreListingSystem, StoreListingPrompt(query, geo, content), origin, cancellationToken);
        var pagination = await DetectPaginationAsync(markdown, pageUrl, cancellationToken);

        return new ListingPageResult
        {
            Products = products,
            NextPageUrl = pagination.NextPageUrl,
            HasLoadMore = pagination.HasLoadMore,
            TotalPages = pagination.TotalPages
        };
    }

    // ══ BRAND crawler ════════════════════════════════════════════════════════════

    /// <summary>
    /// Reads a brand/manufacturer site and asks the LLM to locate its PRODUCT CATALOGUE — the "Products"/
    /// "Shop"/"Catalog" section and the product-line/series pages for the wanted <paramref name="category"/>,
    /// explicitly NOT the marketing homepage. Every URL is absolutized against <paramref name="brandUrl"/>.
    /// Returns an empty (no-catalogue) assessment on any failure.
    /// </summary>
    public async Task<BrandCatalogAssessment> AssessBrandCatalogAsync(
        string brandUrl, string markdown, string category, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(markdown) || !Uri.TryCreate(NormalizeUrl(brandUrl), UriKind.Absolute, out var origin))
        {
            return new BrandCatalogAssessment();
        }

        var content = Crop(CleanForExtraction(markdown), AssessmentMaxChars);
        try
        {
            var dto = await CrawlJsonAsync<BrandCatalogDto>(
                BrandAssessSystem, BrandAssessPrompt(brandUrl, category, content), cancellationToken);
            return dto is null ? new BrandCatalogAssessment() : MapBrandCatalog(dto, origin);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log($"brand catalogue assessment failed for '{brandUrl}': {ex.Message}");
            return new BrandCatalogAssessment();
        }
    }

    /// <summary>
    /// Parses one BRAND catalogue / product-line page: extracts its product MODELS with a spec focus (model
    /// name/number, brand, key specs, features, product URL, image — prices are usually absent on brand sites
    /// and omitted rather than invented) and detects pagination. Best-effort.
    /// </summary>
    public async Task<ListingPageResult> ExtractBrandModelsAsync(
        string markdown, string pageUrl, string category, GeoProfile geo, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return new ListingPageResult();
        }

        var origin = OriginOf(pageUrl);
        var content = Crop(CleanForExtraction(markdown), ListingMaxChars);
        var products = await ExtractCrawlProductsAsync(
            BrandModelsSystem, BrandModelsPrompt(category, content), origin, cancellationToken);
        var pagination = await DetectPaginationAsync(markdown, pageUrl, cancellationToken);

        return new ListingPageResult
        {
            Products = products,
            NextPageUrl = pagination.NextPageUrl,
            HasLoadMore = pagination.HasLoadMore,
            TotalPages = pagination.TotalPages
        };
    }

    // ══ PRODUCT DETAIL extractor ═════════════════════════════════════════════════

    /// <summary>
    /// Mines a single product DETAIL page for its full record: all images, the complete spec sheet, price +
    /// currency, stock, description, features, buyer reviews, related products, and seller. Folds it onto
    /// <paramref name="listing"/> (never discarding a known value for a null one) and returns both the folded
    /// listing and the rich <see cref="ProductDetail"/> for persistence. Returns the listing unchanged (and a
    /// null detail) on failure.
    /// </summary>
    public async Task<(ProductListing Listing, ProductDetail? Detail)> ExtractProductDetailAsync(
        string markdown, ProductListing listing, GeoProfile geo, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return (listing, null);
        }

        var origin = OriginOf(listing.Url);
        var content = Crop(CleanForExtraction(markdown), DetailMaxChars);
        try
        {
            var dto = await CrawlJsonAsync<ProductDetailDto>(
                ProductDetailSystem, ProductDetailPrompt(listing, geo, content), cancellationToken);
            if (dto is null)
            {
                return (listing, null);
            }

            var detail = MapProductDetail(dto, origin);
            return (FoldProductDetail(listing, detail), detail);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log($"product detail extraction failed for '{listing.Url}': {ex.Message}");
            return (listing, null);
        }
    }

    // ══ Shared: relevance classify ═══════════════════════════════════════════════

    /// <summary>
    /// Runs the shared relevance gate over crawl-discovered listings — "is this item itself a &lt;query&gt;?"
    /// — dropping the ones that aren't, with the same sanity guard as the grid's gate: an implausible
    /// drop-(nearly)-everything verdict is ignored so the crawl can prune noise but never empty its results.
    /// </summary>
    public async Task<IReadOnlyList<ProductListing>> ClassifyListingsAsync(
        string query, IReadOnlyList<ProductListing> listings, CancellationToken cancellationToken = default)
    {
        if (listings.Count == 0)
        {
            return listings;
        }

        try
        {
            var labels = listings
                .Select(l => string.Join(" — ", new[] { l.Name, l.Brand, l.Model }
                    .Where(s => !string.IsNullOrWhiteSpace(s))))
                .ToList();

            string text;
            using (LlmCallSiteScope.Enter(LlmCallSites.Relevance))
            {
                text = await _llm.CompleteTextAsync(
                    PromptTemplates.RelevanceGateSystem,
                    PromptTemplates.RelevanceGate(query, labels, _options.RelevancePolicy.Negatives),
                    cancellationToken).ConfigureAwait(false);
            }

            var dto = LlmJson.Deserialize<RelevanceVerdictsDto>(text);
            if (dto?.Drop is not { Count: > 0 } drop)
            {
                return listings;
            }

            var kept = ApplyDropIndices(listings, drop);
            if (kept.Count == listings.Count)
            {
                return listings;
            }

            // Same guard as FilterRelevantModelsAsync: a verdict that wipes (nearly) the whole set is far
            // more likely a gate misfire (or a prompt-injected product name) than genuine noise — distrust it.
            if (kept.Count < Math.Max(1, listings.Count / 5))
            {
                Log($"crawl relevance gate flagged {listings.Count - kept.Count}/{listings.Count} — implausible, ignoring");
                return listings;
            }

            Log($"🧹 Crawl relevance gate removed {listings.Count - kept.Count} item(s) that aren't a {query}.");
            return kept;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log($"crawl relevance gate failed: {ex.Message}");
            return listings;
        }
    }

    // ══ Shared internals ═════════════════════════════════════════════════════════

    /// <summary>One metered LLM call under the Crawl call-site, deserialized to <typeparamref name="T"/> (null on unparseable).</summary>
    private async Task<T?> CrawlJsonAsync<T>(string system, string prompt, CancellationToken ct) where T : class
    {
        string text;
        using (LlmCallSiteScope.Enter(LlmCallSites.Crawl))
        {
            text = await _llm.CompleteTextAsync(system, prompt, ct).ConfigureAwait(false);
        }

        var dto = LlmJson.Deserialize<T>(text);
        if (dto is null)
        {
            Log($"crawl call returned unparseable JSON for {typeof(T).Name}");
        }

        return dto;
    }

    /// <summary>
    /// Runs one extraction call (store- or brand-focused per the supplied prompt), maps each card to a
    /// <see cref="ProductListing"/>, absolutizing its product/image URLs against <paramref name="origin"/> and
    /// dropping cards without a usable name. Returns empty on any failure.
    /// </summary>
    private async Task<IReadOnlyList<ProductListing>> ExtractCrawlProductsAsync(
        string system, string prompt, Uri? origin, CancellationToken ct)
    {
        try
        {
            var dto = await CrawlJsonAsync<CrawlListingDto>(system, prompt, ct);
            if (dto?.Products is not { Count: > 0 } products)
            {
                return Array.Empty<ProductListing>();
            }

            var listings = new List<ProductListing>(products.Count);
            foreach (var p in products)
            {
                if (PickName(p.Name, p.Model) is not { } name)
                {
                    continue; // no usable name (URL/page-title/noise) — drop the card
                }

                listings.Add(new ProductListing
                {
                    Name = name,
                    Brand = Blank(p.Brand),
                    Model = Blank(p.Model),
                    Sku = Blank(p.Sku),
                    Price = ParseDecimal(p.Price),
                    Currency = Blank(p.Currency),
                    Availability = Blank(p.Availability),
                    Url = origin is not null ? AbsolutizeUrl(p.Url, origin) : Blank(p.Url),
                    ImageUrl = origin is not null ? AbsolutizeUrl(p.ImageUrl, origin) : Blank(p.ImageUrl),
                    Specs = p.Specs is { Count: > 0 } ? CleanSpecs(p.Specs) : new Dictionary<string, string>()
                });
            }

            return listings;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log($"crawl product extraction failed: {ex.Message}");
            return Array.Empty<ProductListing>();
        }
    }

    /// <summary>
    /// One LLM call over the page edges to find the "next page" link / load-more control / total page count.
    /// <paramref name="pageUrl"/> anchors relative next-links to an absolute URL. Empty result on any failure.
    /// </summary>
    private async Task<ListingPageResult> DetectPaginationAsync(
        string markdown, string pageUrl, CancellationToken cancellationToken)
    {
        var origin = OriginOf(pageUrl);
        var content = Crop(CleanForExtraction(markdown), PaginationMaxChars);
        try
        {
            var dto = await CrawlJsonAsync<PaginationDto>(
                PaginationSystem, PaginationPrompt(pageUrl, content), cancellationToken);
            if (dto is null)
            {
                return new ListingPageResult();
            }

            var next = origin is not null ? AbsolutizeUrl(dto.NextPageUrl, origin) : dto.NextPageUrl;
            return new ListingPageResult
            {
                NextPageUrl = next,
                HasLoadMore = dto.HasLoadMore ?? false,
                TotalPages = dto.TotalPages is > 0 ? dto.TotalPages : null
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log($"pagination detection failed for '{pageUrl}': {ex.Message}");
            return new ListingPageResult();
        }
    }

    // ══ Pure mapping helpers (unit-testable without an LLM) ══════════════════════

    /// <summary>Maps the store-assessment DTO onto a <see cref="StoreAssessment"/> (absolutizing URLs, keeping the search placeholder).</summary>
    internal static StoreAssessment MapStoreAssessment(StoreAssessmentDto dto, Uri origin) => new()
    {
        Platform = Blank(dto.Platform),
        ListingUrls = AbsolutizeAll(dto.ListingUrls, origin),
        SearchUrlTemplate = AbsolutizeSearchTemplate(dto.SearchUrl, origin),
        SitemapUrl = AbsolutizeUrl(dto.SitemapUrl, origin),
        ApiEndpoints = AbsolutizeAll(dto.ApiEndpoints, origin),
        RecommendedApproach = ParseApproach(dto.Approach),
        Notes = Blank(dto.Notes)
    };

    /// <summary>Maps the brand-catalogue DTO onto a <see cref="BrandCatalogAssessment"/> (absolutizing URLs).</summary>
    internal static BrandCatalogAssessment MapBrandCatalog(BrandCatalogDto dto, Uri origin) => new()
    {
        CatalogUrl = AbsolutizeUrl(dto.CatalogUrl, origin),
        ProductLineUrls = AbsolutizeAll(dto.ProductLineUrls, origin),
        Platform = Blank(dto.Platform),
        Notes = Blank(dto.Notes)
    };

    /// <summary>Maps the product-detail DTO onto a <see cref="ProductDetail"/> (absolutizing image URLs).</summary>
    internal static ProductDetail MapProductDetail(ProductDetailDto dto, Uri? origin) => new()
    {
        Name = Blank(dto.Name),
        Brand = Blank(dto.Brand),
        Sku = Blank(dto.Sku),
        Images = (dto.Images ?? new List<string>())
            .Select(i => origin is not null ? AbsolutizeUrl(i, origin) : Blank(i))
            .Where(i => i is not null).Select(i => i!).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        Price = ParseDecimal(dto.Price),
        Currency = Blank(dto.Currency),
        Availability = Blank(dto.Availability),
        Description = Blank(dto.Description),
        Specs = dto.Specs is { Count: > 0 } ? CleanSpecs(dto.Specs) : new Dictionary<string, string>(),
        Features = CleanList(dto.Features).ToList(),
        RelatedProducts = CleanList(dto.RelatedProducts).ToList(),
        Reviews = (dto.Reviews ?? new List<ProductReviewDto>())
            .Where(r => !string.IsNullOrWhiteSpace(r.Text))
            .Select(r => new ProductReview(r.Text!.Trim(), r.Rating, Blank(r.Author)))
            .ToList(),
        Seller = Blank(dto.Seller)
    };

    /// <summary>
    /// Removes the listed indices from <paramref name="items"/> (ignoring out-of-range/duplicate indices),
    /// keeping everything else — the gate fails open per item. Generic so the grid and the crawl share it.
    /// </summary>
    internal static IReadOnlyList<T> ApplyDropIndices<T>(IReadOnlyList<T> items, IReadOnlyList<int> dropIndices)
    {
        var drop = new HashSet<int>(dropIndices.Where(i => i >= 0 && i < items.Count));
        return drop.Count == 0 ? items : items.Where((_, i) => !drop.Contains(i)).ToList();
    }

    /// <summary>Folds a product-detail record onto a listing, preferring the fuller detail-page name but keeping known offer data.</summary>
    private static ProductListing FoldProductDetail(ProductListing listing, ProductDetail d)
    {
        var specs = new Dictionary<string, string>(listing.Specs);
        foreach (var (k, v) in d.Specs)
        {
            specs[k] = v;
        }
        if (d.Description is { } desc)
        {
            specs["description"] = desc;
        }
        if (d.Features.Count > 0)
        {
            specs["features"] = string.Join(" · ", d.Features);
        }
        if (d.RelatedProducts.Count > 0)
        {
            specs["related_products"] = string.Join(" · ", d.RelatedProducts);
        }

        var reviews = d.Reviews.Count > 0 ? d.Reviews : listing.RatedReviews;

        return listing with
        {
            Name = d.Name ?? listing.Name,           // the detail page carries the fuller, authoritative name
            Brand = listing.Brand ?? d.Brand,
            Sku = listing.Sku ?? d.Sku,
            Price = listing.Price ?? d.Price,         // the listing/offer price is authoritative when present
            Currency = listing.Currency ?? d.Currency,
            Availability = listing.Availability ?? d.Availability,
            Seller = listing.Seller ?? d.Seller,
            ImageUrl = listing.ImageUrl ?? d.Images.FirstOrDefault(),
            Specs = specs,
            RatedReviews = reviews
        };
    }

    /// <summary>Absolutizes a list of possibly-relative URLs against an origin, dropping junk and duplicates.</summary>
    private static IReadOnlyList<string> AbsolutizeAll(IEnumerable<string>? urls, Uri origin) =>
        (urls ?? Enumerable.Empty<string>())
            .Select(u => AbsolutizeUrl(u, origin))
            .Where(u => u is not null).Select(u => u!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Absolutizes a possibly-relative URL against an origin; drops junk (fragments, javascript:, unparseable).</summary>
    internal static string? AbsolutizeUrl(string? candidate, Uri origin)
    {
        var s = candidate?.Trim();
        if (string.IsNullOrWhiteSpace(s) || s.StartsWith('#') ||
            s.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("tel:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Uri.TryCreate(origin, s, out var abs) && (abs.Scheme == Uri.UriSchemeHttp || abs.Scheme == Uri.UriSchemeHttps)
            ? abs.ToString()
            : null;
    }

    /// <summary>
    /// Absolutizes a search template while preserving its <c>{query}</c> placeholder — the placeholder would
    /// be percent-encoded by <see cref="Uri"/>, so we swap in a sentinel, absolutize, then swap back.
    /// </summary>
    private static string? AbsolutizeSearchTemplate(string? template, Uri origin)
    {
        if (string.IsNullOrWhiteSpace(template) || !template.Contains("{query}", StringComparison.Ordinal))
        {
            return null;
        }

        const string sentinel = "__DALEEL_Q__";
        var abs = AbsolutizeUrl(template.Replace("{query}", sentinel, StringComparison.Ordinal), origin);
        return abs?.Replace(sentinel, "{query}", StringComparison.Ordinal);
    }

    private static CrawlApproach ParseApproach(string? approach) => approach?.Trim().ToLowerInvariant() switch
    {
        "search" => CrawlApproach.Search,
        "category" or "collection" or "listing" => CrawlApproach.Category,
        "api" or "json" => CrawlApproach.Api,
        "sitemap" => CrawlApproach.Sitemap,
        _ => CrawlApproach.Unknown
    };

    private static Dictionary<string, string> CleanSpecs(IReadOnlyDictionary<string, string> specs)
    {
        var clean = new Dictionary<string, string>();
        foreach (var (k, v) in specs)
        {
            if (!string.IsNullOrWhiteSpace(k) && !string.IsNullOrWhiteSpace(v))
            {
                clean[k.Trim()] = v.Trim();
            }
        }
        return clean;
    }

    private static Uri? OriginOf(string? url) =>
        Uri.TryCreate(NormalizeUrl(url), UriKind.Absolute, out var u) ? u : null;

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>Reads a price the LLM may have emitted as a number or a string like "1,299.00".</summary>
    private static decimal? ParseDecimal(JsonElement? price)
    {
        if (price is not { } p)
        {
            return null;
        }

        return p.ValueKind switch
        {
            JsonValueKind.Number when p.TryGetDecimal(out var n) => n,
            JsonValueKind.String when PriceParser.TryParse(p.GetString(), out var m) => m.Amount,
            _ => null
        };
    }

    private static string NormalizeUrl(string? url)
    {
        var s = url?.Trim() ?? string.Empty;
        return s.Length > 0 && !s.Contains("://", StringComparison.Ordinal) ? "https://" + s : s;
    }

    private static string Crop(string content, int maxChars) =>
        content.Length <= maxChars ? content : content[..maxChars];

    // ══ Wire DTOs (LLM JSON shapes) ══════════════════════════════════════════════

    internal sealed class StoreAssessmentDto
    {
        [JsonPropertyName("platform")] public string? Platform { get; set; }
        [JsonPropertyName("listingUrls")] public List<string>? ListingUrls { get; set; }
        [JsonPropertyName("searchUrl")] public string? SearchUrl { get; set; }
        [JsonPropertyName("sitemapUrl")] public string? SitemapUrl { get; set; }
        [JsonPropertyName("apiEndpoints")] public List<string>? ApiEndpoints { get; set; }
        [JsonPropertyName("approach")] public string? Approach { get; set; }
        [JsonPropertyName("notes")] public string? Notes { get; set; }
    }

    internal sealed class BrandCatalogDto
    {
        [JsonPropertyName("catalogUrl")] public string? CatalogUrl { get; set; }
        [JsonPropertyName("productLineUrls")] public List<string>? ProductLineUrls { get; set; }
        [JsonPropertyName("platform")] public string? Platform { get; set; }
        [JsonPropertyName("notes")] public string? Notes { get; set; }
    }

    private sealed class CrawlListingDto
    {
        [JsonPropertyName("products")] public List<CrawlProductDto>? Products { get; set; }
    }

    private sealed class CrawlProductDto
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("brand")] public string? Brand { get; set; }
        [JsonPropertyName("model")] public string? Model { get; set; }
        [JsonPropertyName("sku")] public string? Sku { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("imageUrl")] public string? ImageUrl { get; set; }
        [JsonPropertyName("price")] public JsonElement? Price { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("availability")] public string? Availability { get; set; }
        [JsonPropertyName("specs")] public Dictionary<string, string>? Specs { get; set; }
    }

    private sealed class PaginationDto
    {
        [JsonPropertyName("nextPageUrl")] public string? NextPageUrl { get; set; }
        [JsonPropertyName("hasLoadMore")] public bool? HasLoadMore { get; set; }
        [JsonPropertyName("totalPages")] public int? TotalPages { get; set; }
    }

    internal sealed class ProductDetailDto
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("brand")] public string? Brand { get; set; }
        [JsonPropertyName("sku")] public string? Sku { get; set; }
        [JsonPropertyName("images")] public List<string>? Images { get; set; }
        [JsonPropertyName("price")] public JsonElement? Price { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("availability")] public string? Availability { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("specs")] public Dictionary<string, string>? Specs { get; set; }
        [JsonPropertyName("features")] public List<string>? Features { get; set; }
        [JsonPropertyName("relatedProducts")] public List<string>? RelatedProducts { get; set; }
        [JsonPropertyName("reviews")] public List<ProductReviewDto>? Reviews { get; set; }
        [JsonPropertyName("seller")] public string? Seller { get; set; }
    }

    internal sealed class ProductReviewDto
    {
        [JsonPropertyName("text")] public string? Text { get; set; }
        [JsonPropertyName("rating")] public double? Rating { get; set; }
        [JsonPropertyName("author")] public string? Author { get; set; }
    }

    // ══ Prompts — STORE ══════════════════════════════════════════════════════════

    private const string StoreAssessSystem =
        "You are an e-commerce store navigator. You are given an online store's landing page as markdown. " +
        "Figure out the store platform and the best ways to reach its product catalogue. " +
        "Respond with ONLY a JSON object, no prose.";

    private static string StoreAssessPrompt(string url, string query, string content) =>
        $$"""
        Store URL: {{url}}
        The shopper is looking for: "{{query}}"

        Return this exact JSON shape:
        {
          "platform": "Shopify" | "WooCommerce" | "Magento" | "custom" | null,
          "listingUrls": ["<category/collection/listing page URLs found on the page>"],
          "searchUrl": "<the store's search URL with {query} where the search term goes, or null>",
          "sitemapUrl": "<sitemap URL if linked, else null>",
          "apiEndpoints": ["<structured product endpoints, e.g. /products.json for Shopify>"],
          "approach": "search" | "category" | "api" | "sitemap",
          "notes": "<one short line explaining the chosen approach>"
        }

        Rules:
        - Prefer "search" when the store has a working search and the shopper's query is specific.
        - Prefer "api" for Shopify (/products.json or /collections/<name>/products.json) when detected.
        - URLs may be relative; keep them as they appear. Only include a searchUrl if you can see the real search path.

        Store landing page markdown:
        {{content}}
        """;

    private const string StoreListingSystem =
        "You extract product CARDS from an e-commerce store listing/search/category page. Focus on what a " +
        "shopper needs to buy: price, currency, stock, and the link to each product. " +
        "Respond with ONLY a JSON object, no prose. Use null for anything not shown — never invent a price.";

    private static string StoreListingPrompt(string query, GeoProfile geo, string content) =>
        $$"""
        The shopper is looking for: "{{query}}". Target market: {{geo.Country}} (currency {{geo.Currency}}).

        Extract EVERY product card on this store listing page as:
        {
          "products": [
            {
              "name": "<product name>",
              "brand": "<brand or null>",
              "model": "<model number or null>",
              "sku": "<GTIN/UPC/EAN/MPN if shown, else null>",
              "url": "<link to the product's detail page>",
              "imageUrl": "<product image URL or null>",
              "price": <price as a number, or null>,
              "currency": "<ISO currency code, e.g. {{geo.Currency}}, or null>",
              "availability": "<in stock / out of stock / preorder, or null>"
            }
          ]
        }

        Store listing page markdown:
        {{content}}
        """;

    // ══ Prompts — BRAND ══════════════════════════════════════════════════════════

    private const string BrandAssessSystem =
        "You are a brand/manufacturer website navigator. The marketing homepage is NOT the product catalogue. " +
        "Your job is to find the PRODUCTS/CATALOG section and the specific product-line/series pages for the " +
        "wanted product category. Respond with ONLY a JSON object, no prose.";

    private static string BrandAssessPrompt(string url, string category, string content) =>
        $$"""
        Brand site URL: {{url}}
        Wanted product category: "{{category}}"

        Return this exact JSON shape:
        {
          "catalogUrl": "<URL of the main Products/Shop/Catalog section, or null>",
          "productLineUrls": ["<URLs of product line/series/category pages matching the wanted category>"],
          "platform": "<site platform/framework if discernible, else null>",
          "notes": "<one short line on where the catalogue lives>"
        }

        Rules:
        - IGNORE marketing/press/about/support pages — you want where the actual products are browsable.
        - Brand catalogues are organised by product LINE or SERIES (e.g. TVs → "OLED evo" → C4/G4). List the
          line/series/category pages that match the wanted category, most relevant first.
        - URLs may be relative; keep them as they appear.

        Brand landing page markdown:
        {{content}}
        """;

    private const string BrandModelsSystem =
        "You extract product MODELS from a brand/manufacturer catalogue or product-line page. Focus on the " +
        "product itself: model name/number, key specs, and features. Brand sites usually DON'T show prices — " +
        "omit price unless it is explicitly shown. Respond with ONLY a JSON object, no prose; never invent values.";

    private static string BrandModelsPrompt(string category, string content) =>
        $$"""
        Wanted product category: "{{category}}".

        Extract EVERY product model on this brand catalogue page as:
        {
          "products": [
            {
              "name": "<model name>",
              "brand": "<brand/manufacturer>",
              "model": "<model number/code, e.g. OLED55C4>",
              "url": "<link to the model's detail page>",
              "imageUrl": "<model image URL or null>",
              "specs": { "<attribute>": "<value>" },
              "price": <number if a price is explicitly shown, else null>,
              "currency": "<ISO code if a price is shown, else null>"
            }
          ]
        }

        Brand catalogue page markdown:
        {{content}}
        """;

    // ══ Prompts — PRODUCT DETAIL ═════════════════════════════════════════════════

    private const string ProductDetailSystem =
        "You extract a single product's COMPLETE record from its product detail page — every image, the full " +
        "spec sheet, price, description, features, buyer reviews, related products, and the seller. " +
        "Respond with ONLY a JSON object, no prose. Use null / empty arrays for anything not present — never invent.";

    private static string ProductDetailPrompt(ProductListing listing, GeoProfile geo, string content) =>
        $$"""
        This is the detail page for a product listed as: "{{listing.Name}}".
        Target market: {{geo.Country}} (currency {{geo.Currency}}).

        Return this exact JSON shape:
        {
          "name": "<full product name>",
          "brand": "<brand/manufacturer or null>",
          "sku": "<GTIN/UPC/EAN/MPN if shown, else null>",
          "images": ["<every product image URL on the page, primary first>"],
          "price": <price as a number, or null>,
          "currency": "<ISO currency code, e.g. {{geo.Currency}}, or null>",
          "availability": "<in stock / out of stock / preorder, or null>",
          "description": "<the full product description, trimmed, or null>",
          "specs": { "<attribute>": "<value>" },
          "features": ["<bullet-point highlights/features>"],
          "relatedProducts": ["<names of related/recommended products linked on the page>"],
          "reviews": [ { "text": "<review text>", "rating": <1-5 or null>, "author": "<name or null>" } ],
          "seller": "<seller/store name if shown, else null>"
        }

        Product detail page markdown:
        {{content}}
        """;

    private const string PaginationSystem =
        "You detect pagination on a product listing/catalogue page. Respond with ONLY a JSON object, no prose.";

    private static string PaginationPrompt(string url, string content) =>
        $$"""
        Current listing page URL: {{url}}

        Return this exact JSON shape:
        {
          "nextPageUrl": "<URL of the NEXT listing page, or null if this is the last page>",
          "hasLoadMore": true | false,
          "totalPages": <total number of listing pages if shown, else null>
        }

        Rules:
        - nextPageUrl is the link to page N+1 (a "Next", "›", or numbered-pagination link). It may be relative.
        - Set hasLoadMore true only when the page uses a "Load more"/infinite-scroll control instead of page links.
        - Return null nextPageUrl if there is genuinely no further page.

        Listing page markdown:
        {{content}}
        """;
}
