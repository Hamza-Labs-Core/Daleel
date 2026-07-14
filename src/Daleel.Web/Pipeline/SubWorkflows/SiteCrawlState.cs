using Daleel.Core.Models;

namespace Daleel.Web.Pipeline.SubWorkflows;

/// <summary>
/// Per-site state for <see cref="SiteCrawlWorkflow"/> — the LLM-driven crawler that navigates one store or
/// brand website to find products. Seeded with the site URL + the shopper's query; the activities render
/// each page (CF Browser via the metered scraper), let the LLM decide how to navigate, accumulate the
/// discovered products onto <see cref="Discovered"/>, and persist them. Like the other
/// <see cref="SubWorkflowState"/>s this holds ONLY serializable data — the live agent + progress sink come
/// from <see cref="SubWorkflowServices"/>.
/// </summary>
public sealed class SiteCrawlState : SubWorkflowState
{
    // ── Inputs (seeded by the dispatcher) ────────────────────────────────────────

    /// <summary>The site to crawl (a store or brand homepage/landing URL).</summary>
    public string SiteUrl { get; set; } = string.Empty;

    /// <summary>The shopper's original search query — the crawler navigates the site toward it.</summary>
    public string Query { get; set; } = string.Empty;

    /// <summary>Display name of the store/brand (used as the product source + in timeline events).</summary>
    public string SiteName { get; set; } = string.Empty;

    /// <summary>What kind of site the caller expects this to be (store vs brand); the assessment can override it.</summary>
    public SiteKind ExpectedKind { get; set; } = SiteKind.Store;

    // ── Working state (filled by the activities) ─────────────────────────────────

    /// <summary>The LLM's assessment of the homepage — how to reach the catalogue.</summary>
    public SiteAssessment Assessment { get; set; } = new();

    /// <summary>The concrete listing entry point the navigation step chose (search/category/api/sitemap URL).</summary>
    public string? ListingUrl { get; set; }

    /// <summary>The next-page URL carried between the extract step and the paginate loop (null ⇒ stop).</summary>
    public string? PaginationCursor { get; set; }

    /// <summary>How many listing pages have been fetched so far (bounded by <c>PipelineLimits.CrawlMaxPages</c>).</summary>
    public int PagesFetched { get; set; }

    /// <summary>Every product discovered across the crawled listing pages (deduped by URL/identity).</summary>
    public List<ProductListing> Discovered { get; init; } = new();

    // ── Outputs (read back by the dispatcher / recorded to the timeline) ──────────

    /// <summary>How many products survived the relevance classifier and were persisted as EntityDocuments.</summary>
    public int Persisted { get; set; }

    /// <summary>How many priced products were written to the <c>ScrapedPrice</c> series.</summary>
    public int PricesRecorded { get; set; }
}
