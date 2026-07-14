using Daleel.Core.Models;

namespace Daleel.Web.Pipeline.SubWorkflows;

/// <summary>
/// Per-store state for <see cref="StoreCrawlWorkflow"/> — the LLM crawler for e-commerce STORES. Seeded with
/// the store URL + the shopper's query; the activities render each page (CF Browser), let the LLM navigate
/// commerce-style (search / categories / product API), accumulate priced product cards onto
/// <see cref="Discovered"/>, deep-dive the top matches, and persist them. Serializable data only — the live
/// agent + progress sink come from <see cref="SubWorkflowServices"/>.
/// </summary>
public sealed class StoreCrawlState : SubWorkflowState
{
    // ── Inputs ───────────────────────────────────────────────────────────────────

    /// <summary>The store homepage/landing URL to crawl.</summary>
    public string SiteUrl { get; set; } = string.Empty;

    /// <summary>The shopper's original search query — the crawler navigates the store toward it.</summary>
    public string Query { get; set; } = string.Empty;

    /// <summary>Display name of the store (used as the product source + in timeline events).</summary>
    public string SiteName { get; set; } = string.Empty;

    // ── Working state ────────────────────────────────────────────────────────────

    /// <summary>The LLM's assessment of the store homepage — platform + the ways into the catalogue.</summary>
    public StoreAssessment Assessment { get; set; } = new();

    /// <summary>The concrete listing entry point the navigation step chose (search/category/api/sitemap URL).</summary>
    public string? ListingUrl { get; set; }

    /// <summary>The next-page URL carried between the extract step and the paginate loop (null ⇒ stop).</summary>
    public string? PaginationCursor { get; set; }

    /// <summary>How many listing pages have been fetched (bounded by <c>PipelineLimits.CrawlMaxPages</c>).</summary>
    public int PagesFetched { get; set; }

    /// <summary>Every product discovered across the crawled listing pages (deduped by URL/identity).</summary>
    public List<ProductListing> Discovered { get; init; } = new();

    // ── Outputs ──────────────────────────────────────────────────────────────────

    /// <summary>How many products were persisted (deep-dived + listing-level remainder).</summary>
    public int Persisted { get; set; }

    /// <summary>How many priced products were written to the <c>ScrapedPrice</c> series.</summary>
    public int PricesRecorded { get; set; }
}
