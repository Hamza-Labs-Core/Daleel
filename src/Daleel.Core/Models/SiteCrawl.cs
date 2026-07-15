namespace Daleel.Core.Models;

/// <summary>
/// The navigation approach the LLM recommends for reaching a STORE's product listings. The store crawler
/// picks the concrete next URL from the matching field on <see cref="StoreAssessment"/> (search box,
/// category page, structured API, or sitemap).
/// </summary>
public enum CrawlApproach
{
    Unknown,

    /// <summary>Use the store's own search with the query (<see cref="StoreAssessment.SearchUrlTemplate"/>).</summary>
    Search,

    /// <summary>Navigate to a category / collection listing page (<see cref="StoreAssessment.ListingUrls"/>).</summary>
    Category,

    /// <summary>Call a structured product endpoint, e.g. Shopify <c>/products.json</c> (<see cref="StoreAssessment.ApiEndpoints"/>).</summary>
    Api,

    /// <summary>Walk the sitemap (<see cref="StoreAssessment.SitemapUrl"/>).</summary>
    Sitemap
}

/// <summary>
/// The store crawler's read of an e-commerce site's homepage — its platform and the ways into its product
/// catalogue. Produced by <c>AgentService.AssessStoreAsync</c>. Every URL is absolutized against the site
/// origin; <see cref="SearchUrlTemplate"/> keeps a <c>{query}</c> placeholder the navigation step fills in.
/// </summary>
public sealed record StoreAssessment
{
    /// <summary>Detected e-commerce platform, e.g. "Shopify", "WooCommerce", "Magento", "custom". Null when unknown.</summary>
    public string? Platform { get; init; }

    /// <summary>Absolute category/collection/listing URLs the LLM found, most promising first.</summary>
    public IReadOnlyList<string> ListingUrls { get; init; } = Array.Empty<string>();

    /// <summary>
    /// The store's search URL with a <c>{query}</c> placeholder (e.g. <c>https://x.com/search?q={query}</c>),
    /// or null when the store exposes no usable search.
    /// </summary>
    public string? SearchUrlTemplate { get; init; }

    /// <summary>Absolute sitemap URL when the LLM spotted one (e.g. <c>/sitemap.xml</c>), else null.</summary>
    public string? SitemapUrl { get; init; }

    /// <summary>
    /// Structured product endpoints the LLM detected (e.g. Shopify <c>/products.json</c>,
    /// <c>/collections/all/products.json</c>), absolutized. Empty when none.
    /// </summary>
    public IReadOnlyList<string> ApiEndpoints { get; init; } = Array.Empty<string>();

    /// <summary>The approach the LLM recommends taking first.</summary>
    public CrawlApproach RecommendedApproach { get; init; } = CrawlApproach.Unknown;

    /// <summary>Free-form one-line rationale from the LLM (logged to the timeline for visibility).</summary>
    public string? Notes { get; init; }

    /// <summary>True when the assessment found at least one concrete way into the catalogue.</summary>
    public bool HasEntryPoint =>
        SearchUrlTemplate is { Length: > 0 } ||
        ListingUrls.Count > 0 ||
        ApiEndpoints.Count > 0 ||
        SitemapUrl is { Length: > 0 };
}

/// <summary>
/// The brand crawler's read of a MANUFACTURER's site — where its product catalogue lives, as opposed to the
/// marketing homepage. Produced by <c>AgentService.AssessBrandCatalogAsync</c>. Brand navigation is
/// fundamentally different from a store's: there is rarely a price-bearing search; instead the models sit
/// under a "Products"/"Catalog" section organised into product lines/series (e.g. LG "OLED evo" → C4/G4).
/// </summary>
public sealed record BrandCatalogAssessment
{
    /// <summary>The absolute URL of the product catalogue landing (the "Products"/"Shop"/"Catalog" section), or null.</summary>
    public string? CatalogUrl { get; init; }

    /// <summary>
    /// Absolute URLs of product-line / series / category pages under the catalogue that match the wanted
    /// product category (e.g. the "TVs" or "Air Conditioners" line), most relevant first.
    /// </summary>
    public IReadOnlyList<string> ProductLineUrls { get; init; } = Array.Empty<string>();

    /// <summary>Detected site platform/framework when discernible, else null.</summary>
    public string? Platform { get; init; }

    /// <summary>Free-form one-line rationale from the LLM (logged to the timeline).</summary>
    public string? Notes { get; init; }

    /// <summary>True when the LLM located a catalogue entry point (a catalogue landing or at least one product line).</summary>
    public bool HasCatalog => CatalogUrl is { Length: > 0 } || ProductLineUrls.Count > 0;

    /// <summary>The catalogue entry points to walk, catalogue landing first then the matching product lines.</summary>
    public IReadOnlyList<string> EntryPoints =>
        (CatalogUrl is { Length: > 0 } c ? new[] { c } : Array.Empty<string>())
            .Concat(ProductLineUrls)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}

/// <summary>
/// The result of the LLM parsing one listing/catalogue page: the product cards it found plus the pagination
/// signals that tell the crawler whether (and where) to fetch the next page. Shared by the store and brand
/// crawlers (each feeds it a differently-focused extraction, but the page-walking shape is identical).
/// </summary>
public sealed record ListingPageResult
{
    /// <summary>The product summaries extracted from this page.</summary>
    public IReadOnlyList<ProductListing> Products { get; init; } = Array.Empty<ProductListing>();

    /// <summary>Absolute URL of the next listing page, or null when this is the last page.</summary>
    public string? NextPageUrl { get; init; }

    /// <summary>True when the page paginates via a "load more" / infinite-scroll control rather than a next link.</summary>
    public bool HasLoadMore { get; init; }

    /// <summary>Total number of listing pages when the page advertised it (e.g. "Page 1 of 8"), else null.</summary>
    public int? TotalPages { get; init; }
}

/// <summary>
/// The full record the LLM extracts from a single product DETAIL page (produced by
/// <c>AgentService.ExtractProductDetailAsync</c>): everything a product page carries — all images, the full
/// spec sheet, price, description, features, buyer reviews, related products, and the seller. Folded onto the
/// product's <see cref="ProductListing"/> and persisted (the schema-less R2 document carries the extras).
/// </summary>
public sealed record ProductDetail
{
    public string? Name { get; init; }
    public string? Brand { get; init; }
    public string? Sku { get; init; }

    /// <summary>All product image URLs on the page (primary first).</summary>
    public IReadOnlyList<string> Images { get; init; } = Array.Empty<string>();

    public decimal? Price { get; init; }
    public string? Currency { get; init; }

    /// <summary>Stock status: "in stock" / "out of stock" / "preorder", when shown.</summary>
    public string? Availability { get; init; }

    /// <summary>The product's full description text.</summary>
    public string? Description { get; init; }

    /// <summary>The full technical spec sheet as key/value pairs.</summary>
    public IReadOnlyDictionary<string, string> Specs { get; init; } = new Dictionary<string, string>();

    /// <summary>Bullet-point highlights/features called out on the page.</summary>
    public IReadOnlyList<string> Features { get; init; } = Array.Empty<string>();

    /// <summary>Names of related/recommended products the page links to.</summary>
    public IReadOnlyList<string> RelatedProducts { get; init; } = Array.Empty<string>();

    /// <summary>Buyer reviews scraped from the page (rating + text), when present.</summary>
    public IReadOnlyList<ProductReview> Reviews { get; init; } = Array.Empty<ProductReview>();

    /// <summary>The seller/store name when the page attributes one.</summary>
    public string? Seller { get; init; }
}
