namespace Daleel.Core.Models;

/// <summary>
/// What kind of site the LLM decided it is looking at. Drives how the crawler navigates: a
/// <see cref="Store"/> or <see cref="Marketplace"/> is walked for product listings, a <see cref="Brand"/>
/// site is walked for its own catalogue.
/// </summary>
public enum SiteKind
{
    Unknown,
    Store,
    Brand,
    Marketplace
}

/// <summary>
/// The navigation approach the LLM recommends for reaching this site's product listings. The crawler
/// picks the concrete next URL from the matching field on <see cref="SiteAssessment"/> (search box,
/// category page, structured API, or sitemap).
/// </summary>
public enum CrawlApproach
{
    Unknown,

    /// <summary>Use the site's own search with the query (<see cref="SiteAssessment.SearchUrlTemplate"/>).</summary>
    Search,

    /// <summary>Navigate to a category / collection listing page (<see cref="SiteAssessment.ListingUrls"/>).</summary>
    Category,

    /// <summary>Call a structured product endpoint, e.g. Shopify <c>/products.json</c> (<see cref="SiteAssessment.ApiEndpoints"/>).</summary>
    Api,

    /// <summary>Walk the sitemap (<see cref="SiteAssessment.SitemapUrl"/>).</summary>
    Sitemap
}

/// <summary>
/// The LLM's read of a site's homepage/landing — its type, platform, and the ways in to its product
/// catalogue. Produced by <c>AgentService.AssessSiteAsync</c> and carried on the crawl state so the
/// downstream navigation step can pick the best entry point without re-reading the page.
/// </summary>
/// <remarks>
/// Every URL here is absolutized against the site origin at parse time, so consumers can pass them
/// straight to the scraper. <see cref="SearchUrlTemplate"/> is the one exception: it keeps a
/// <c>{query}</c> placeholder the navigation step substitutes with the URL-encoded search query.
/// </remarks>
public sealed record SiteAssessment
{
    /// <summary>The site classification (store / brand / marketplace), Unknown when the LLM couldn't tell.</summary>
    public SiteKind Kind { get; init; } = SiteKind.Unknown;

    /// <summary>Detected e-commerce platform, e.g. "Shopify", "WooCommerce", "custom". Null when unknown.</summary>
    public string? Platform { get; init; }

    /// <summary>Absolute category/collection/listing URLs the LLM found on the page, most promising first.</summary>
    public IReadOnlyList<string> ListingUrls { get; init; } = Array.Empty<string>();

    /// <summary>
    /// The site's search URL with a <c>{query}</c> placeholder (e.g. <c>https://x.com/search?q={query}</c>),
    /// or null when the site exposes no usable search.
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
/// The result of the LLM parsing one listing page: the product cards it found plus the pagination
/// signals that tell the crawler whether (and where) to fetch the next page.
/// </summary>
public sealed record ListingPageResult
{
    /// <summary>The product summaries extracted from this listing page.</summary>
    public IReadOnlyList<ProductListing> Products { get; init; } = Array.Empty<ProductListing>();

    /// <summary>Absolute URL of the next listing page, or null when this is the last page.</summary>
    public string? NextPageUrl { get; init; }

    /// <summary>True when the page paginates via a "load more" / infinite-scroll control rather than a next link.</summary>
    public bool HasLoadMore { get; init; }

    /// <summary>Total number of listing pages when the page advertised it (e.g. "Page 1 of 8"), else null.</summary>
    public int? TotalPages { get; init; }
}
