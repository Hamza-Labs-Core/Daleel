using System.Text.Json;
using System.Text.Json.Serialization;
using Daleel.Core.Geo;
using Daleel.Core.Llm;
using Daleel.Core.Models;
using Daleel.Core.Pricing;

namespace Daleel.Agent;

/// <summary>
/// The LLM-driven site-crawl half of the agent: the decisions an intelligent crawler makes at each step —
/// <em>assess</em> a site's homepage to find the ways into its catalogue, <em>extract</em> a listing page
/// (products + pagination), <em>deep-dive</em> a product's detail page, and <em>classify</em> which
/// discovered products actually match the query. Each is a metered LLM call stamped with the
/// <see cref="LlmCallSites.Crawl"/> call-site, and each is best-effort by construction (a failed/unparseable
/// call degrades to a safe default) so the crawl can never fault the search.
/// </summary>
/// <remarks>
/// These methods own only the <em>reasoning</em>; the rendering is the caller's job via
/// <see cref="ReadPageAsync"/> (which routes through the metered scraper — Context.dev, then Cloudflare
/// Browser Rendering — and SSRF-guards every URL). Keeping the reasoning here means the whole crawl inherits
/// the agent's metering, per-call-site model routing, and unit-testability with a fake LLM.
/// </remarks>
public sealed partial class AgentService
{
    /// <summary>Homepage markdown is cropped to this before the assessment call — the nav/links that
    /// reveal a site's structure live near the top, and a whole rendered store page is mostly product noise.</summary>
    private const int AssessmentMaxChars = 12_000;

    /// <summary>Pagination controls live at the page edges; a bounded slice keeps the detect call cheap.</summary>
    private const int PaginationMaxChars = 6_000;

    /// <summary>A product detail page is cropped to this before the deep-dive call.</summary>
    private const int DeepDiveMaxChars = 16_000;

    // ── 1. Assess ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads a site's homepage/landing markdown and asks the LLM what kind of site it is and how to reach
    /// its product catalogue — platform, category/listing URLs, a search-URL template, a sitemap, and any
    /// structured product endpoints (e.g. Shopify <c>/products.json</c>). Every URL is absolutized against
    /// <paramref name="siteUrl"/>. Returns an empty (Unknown, no-entry-point) assessment on any failure.
    /// </summary>
    public async Task<SiteAssessment> AssessSiteAsync(
        string siteUrl, string markdown, string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(markdown) || !Uri.TryCreate(NormalizeUrl(siteUrl), UriKind.Absolute, out var origin))
        {
            return new SiteAssessment();
        }

        var content = Crop(CleanForExtraction(markdown), AssessmentMaxChars);
        try
        {
            string text;
            using (LlmCallSiteScope.Enter(LlmCallSites.Crawl))
            {
                text = await _llm.CompleteTextAsync(
                    CrawlAssessSystem, CrawlAssessPrompt(siteUrl, query, content), cancellationToken)
                    .ConfigureAwait(false);
            }

            var dto = LlmJson.Deserialize<SiteAssessmentDto>(text);
            if (dto is null)
            {
                Log("site assessment returned unparseable JSON");
                return new SiteAssessment();
            }

            return MapAssessment(dto, origin);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log($"site assessment failed for '{siteUrl}': {ex.Message}");
            return new SiteAssessment();
        }
    }

    // ── 2. Extract a listing page (products + pagination) ────────────────────────

    /// <summary>
    /// Parses one listing page: extracts its product cards (reusing the robust chunked extraction fan-out)
    /// and, in one cheap extra call, detects the pagination signals (next-page URL, load-more, total pages)
    /// so the caller knows whether to continue. Best-effort: extraction and pagination fail independently and
    /// degrade to "no products" / "no next page".
    /// </summary>
    public async Task<ListingPageResult> ExtractListingAsync(
        string markdown, string pageUrl, string query, GeoProfile geo, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return new ListingPageResult();
        }

        var products = await ExtractProductsFromPageAsync(markdown, query, geo, cancellationToken)
            .ConfigureAwait(false);
        var pagination = await DetectPaginationAsync(markdown, pageUrl, cancellationToken).ConfigureAwait(false);

        return new ListingPageResult
        {
            Products = products,
            NextPageUrl = pagination.NextPageUrl,
            HasLoadMore = pagination.HasLoadMore,
            TotalPages = pagination.TotalPages
        };
    }

    /// <summary>
    /// One LLM call over the page edges to find the "next page" link / load-more control / total page count.
    /// <paramref name="pageUrl"/> anchors relative next-links to an absolute URL. Returns an empty result
    /// (no next page) on any failure.
    /// </summary>
    private async Task<ListingPageResult> DetectPaginationAsync(
        string markdown, string pageUrl, CancellationToken cancellationToken)
    {
        Uri.TryCreate(NormalizeUrl(pageUrl), UriKind.Absolute, out var origin);
        var content = Crop(CleanForExtraction(markdown), PaginationMaxChars);
        try
        {
            string text;
            using (LlmCallSiteScope.Enter(LlmCallSites.Crawl))
            {
                text = await _llm.CompleteTextAsync(
                    CrawlPaginationSystem, CrawlPaginationPrompt(pageUrl, content), cancellationToken)
                    .ConfigureAwait(false);
            }

            var dto = LlmJson.Deserialize<PaginationDto>(text);
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

    // ── 3. Deep-dive a product detail page ───────────────────────────────────────

    /// <summary>
    /// Enriches a product summary from its own detail page markdown: full name, brand, SKU, image URLs,
    /// price + currency, stock status, description, spec key/values, and seller. Folds everything it finds
    /// onto <paramref name="listing"/> (never discarding an already-known value for a null LLM one). Returns
    /// the listing unchanged on any failure.
    /// </summary>
    public async Task<ProductListing> DeepDiveProductAsync(
        string markdown, ProductListing listing, GeoProfile geo, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return listing;
        }

        var content = Crop(CleanForExtraction(markdown), DeepDiveMaxChars);
        try
        {
            string text;
            using (LlmCallSiteScope.Enter(LlmCallSites.Crawl))
            {
                text = await _llm.CompleteTextAsync(
                    CrawlDeepDiveSystem, CrawlDeepDivePrompt(listing, geo, content), cancellationToken)
                    .ConfigureAwait(false);
            }

            var dto = LlmJson.Deserialize<DeepDiveDto>(text);
            return dto is null ? listing : MergeDeepDive(listing, dto);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log($"product deep-dive failed for '{listing.Url}': {ex.Message}");
            return listing;
        }
    }

    // ── 4. Classify / filter by relevance to the query ───────────────────────────

    /// <summary>
    /// Runs the shared relevance gate over crawl-discovered listings — "is this item itself a &lt;query&gt;?"
    /// — and drops the ones that aren't (accessories, unrelated products). Best-effort with the same
    /// sanity guard as the grid's gate: an implausible drop-(nearly)-everything verdict is ignored so the
    /// crawl can prune noise but never empty its own results.
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
                return listings; // nothing flagged (or unparseable) — keep everything
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
            return listings; // best-effort — never fault the crawl over a relevance pass
        }
    }

    // ── Pure helpers (unit-testable without an LLM) ──────────────────────────────

    /// <summary>
    /// Maps the LLM's wire DTO onto a <see cref="SiteAssessment"/>: parses the enums leniently, absolutizes
    /// every URL against <paramref name="origin"/>, and preserves the <c>{query}</c> placeholder in the
    /// search template. Pure and static so the mapping is testable with hand-built DTOs.
    /// </summary>
    internal static SiteAssessment MapAssessment(SiteAssessmentDto dto, Uri origin)
    {
        var listingUrls = (dto.ListingUrls ?? new List<string>())
            .Select(u => AbsolutizeUrl(u, origin))
            .Where(u => u is not null)
            .Select(u => u!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var apiEndpoints = (dto.ApiEndpoints ?? new List<string>())
            .Select(u => AbsolutizeUrl(u, origin))
            .Where(u => u is not null)
            .Select(u => u!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // The search template carries a {query} placeholder that must NOT be URL-mangled, so absolutize only
        // the part before it.
        var searchTemplate = AbsolutizeSearchTemplate(dto.SearchUrl, origin);

        return new SiteAssessment
        {
            Kind = ParseKind(dto.Kind),
            Platform = Blank(dto.Platform),
            ListingUrls = listingUrls,
            SearchUrlTemplate = searchTemplate,
            SitemapUrl = AbsolutizeUrl(dto.SitemapUrl, origin),
            ApiEndpoints = apiEndpoints,
            RecommendedApproach = ParseApproach(dto.Approach),
            Notes = Blank(dto.Notes)
        };
    }

    /// <summary>
    /// Removes the listed indices from <paramref name="items"/> (ignoring out-of-range/duplicate indices),
    /// keeping everything else — the gate fails open per item. Generic so both the model grid and the crawl's
    /// listings share one verdict-application semantics.
    /// </summary>
    internal static IReadOnlyList<T> ApplyDropIndices<T>(IReadOnlyList<T> items, IReadOnlyList<int> dropIndices)
    {
        var drop = new HashSet<int>(dropIndices.Where(i => i >= 0 && i < items.Count));
        return drop.Count == 0 ? items : items.Where((_, i) => !drop.Contains(i)).ToList();
    }

    /// <summary>Folds a deep-dive DTO onto a listing, preferring an existing value over a null/blank LLM one.</summary>
    private static ProductListing MergeDeepDive(ProductListing listing, DeepDiveDto dto)
    {
        var specs = new Dictionary<string, string>(listing.Specs);
        if (dto.Specs is not null)
        {
            foreach (var (k, v) in dto.Specs)
            {
                if (!string.IsNullOrWhiteSpace(k) && !string.IsNullOrWhiteSpace(v))
                {
                    specs[k.Trim()] = v.Trim();
                }
            }
        }
        if (Blank(dto.Description) is { } description)
        {
            specs["description"] = description;
        }

        var image = listing.ImageUrl ?? dto.Images?.FirstOrDefault(i => !string.IsNullOrWhiteSpace(i));

        return listing with
        {
            Name = Blank(dto.Name) ?? listing.Name,
            Brand = listing.Brand ?? Blank(dto.Brand),
            Sku = listing.Sku ?? Blank(dto.Sku),
            Price = listing.Price ?? ParseDecimal(dto.Price),
            Currency = listing.Currency ?? Blank(dto.Currency),
            Availability = listing.Availability ?? Blank(dto.Availability),
            Seller = listing.Seller ?? Blank(dto.Seller),
            ImageUrl = image,
            Specs = specs
        };
    }

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

    private static SiteKind ParseKind(string? kind) => kind?.Trim().ToLowerInvariant() switch
    {
        "store" or "retailer" or "shop" => SiteKind.Store,
        "brand" or "manufacturer" => SiteKind.Brand,
        "marketplace" or "classifieds" => SiteKind.Marketplace,
        _ => SiteKind.Unknown
    };

    private static CrawlApproach ParseApproach(string? approach) => approach?.Trim().ToLowerInvariant() switch
    {
        "search" => CrawlApproach.Search,
        "category" or "collection" or "listing" => CrawlApproach.Category,
        "api" or "json" => CrawlApproach.Api,
        "sitemap" => CrawlApproach.Sitemap,
        _ => CrawlApproach.Unknown
    };

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

    // ── Wire DTOs (LLM JSON shapes) ──────────────────────────────────────────────

    /// <summary>Wire shape for the site-assessment LLM output.</summary>
    internal sealed class SiteAssessmentDto
    {
        [JsonPropertyName("kind")] public string? Kind { get; set; }
        [JsonPropertyName("platform")] public string? Platform { get; set; }
        [JsonPropertyName("listingUrls")] public List<string>? ListingUrls { get; set; }
        [JsonPropertyName("searchUrl")] public string? SearchUrl { get; set; }
        [JsonPropertyName("sitemapUrl")] public string? SitemapUrl { get; set; }
        [JsonPropertyName("apiEndpoints")] public List<string>? ApiEndpoints { get; set; }
        [JsonPropertyName("approach")] public string? Approach { get; set; }
        [JsonPropertyName("notes")] public string? Notes { get; set; }
    }

    private sealed class PaginationDto
    {
        [JsonPropertyName("nextPageUrl")] public string? NextPageUrl { get; set; }
        [JsonPropertyName("hasLoadMore")] public bool? HasLoadMore { get; set; }
        [JsonPropertyName("totalPages")] public int? TotalPages { get; set; }
    }

    private sealed class DeepDiveDto
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
        [JsonPropertyName("seller")] public string? Seller { get; set; }
    }

    // ── Prompts ──────────────────────────────────────────────────────────────────

    private const string CrawlAssessSystem =
        "You are a web-crawl navigator. You are given a store or brand website's landing page as markdown. " +
        "Figure out what kind of site it is and the best ways to reach its product catalogue. " +
        "Respond with ONLY a JSON object, no prose.";

    private static string CrawlAssessPrompt(string url, string query, string content) =>
        $$"""
        Site URL: {{url}}
        The shopper is looking for: "{{query}}"

        From the landing page markdown below, return this exact JSON shape:
        {
          "kind": "store" | "brand" | "marketplace",
          "platform": "Shopify" | "WooCommerce" | "Magento" | "custom" | null,
          "listingUrls": ["<category/collection/listing page URLs found on the page>"],
          "searchUrl": "<the site's search URL with {query} where the search term goes, or null>",
          "sitemapUrl": "<sitemap URL if linked, else null>",
          "apiEndpoints": ["<structured product endpoints, e.g. /products.json for Shopify>"],
          "approach": "search" | "category" | "api" | "sitemap",
          "notes": "<one short line explaining the chosen approach>"
        }

        Rules:
        - Prefer "search" when the site has a working search and the shopper's query is specific.
        - Prefer "api" for Shopify (/products.json or /collections/<name>/products.json) when detected.
        - URLs may be relative; keep them as they appear.
        - Only include a searchUrl if you can see the site's real search path; otherwise null.

        Landing page markdown:
        {{content}}
        """;

    private const string CrawlPaginationSystem =
        "You detect pagination on an e-commerce listing page. Respond with ONLY a JSON object, no prose.";

    private static string CrawlPaginationPrompt(string url, string content) =>
        $$"""
        Current listing page URL: {{url}}

        From the listing page markdown below, return this exact JSON shape:
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

    private const string CrawlDeepDiveSystem =
        "You extract a single product's full details from its product detail page. " +
        "Respond with ONLY a JSON object, no prose. Use null for anything not present — never invent values.";

    private static string CrawlDeepDivePrompt(ProductListing listing, GeoProfile geo, string content) =>
        $$"""
        This is the detail page for a product listed as: "{{listing.Name}}"
        Target market: {{geo.Country}} (currency {{geo.Currency}}).

        Return this exact JSON shape:
        {
          "name": "<full product name>",
          "brand": "<brand/manufacturer or null>",
          "sku": "<GTIN/UPC/EAN/MPN if shown, else null>",
          "images": ["<product image URLs>"],
          "price": <price as a number, or null>,
          "currency": "<ISO currency code, e.g. {{geo.Currency}}, or null>",
          "availability": "<in stock / out of stock / preorder, or null>",
          "description": "<the product description, trimmed, or null>",
          "specs": { "<attribute>": "<value>" },
          "seller": "<seller/store name if shown, else null>"
        }

        Product detail page markdown:
        {{content}}
        """;
}
