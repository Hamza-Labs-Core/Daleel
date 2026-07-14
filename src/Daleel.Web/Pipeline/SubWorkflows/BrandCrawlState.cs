using Daleel.Core.Models;

namespace Daleel.Web.Pipeline.SubWorkflows;

/// <summary>
/// Per-brand state for <see cref="BrandCrawlWorkflow"/> — the LLM crawler for MANUFACTURER sites (lg.com,
/// sharp.com …). Seeded with the brand site URL + the product category to look for; the activities locate the
/// product catalogue (not the marketing homepage), walk its product lines/series extracting model specs, then
/// deep-dive and persist. Serializable data only — the live agent + progress sink come from
/// <see cref="SubWorkflowServices"/>.
/// </summary>
public sealed class BrandCrawlState : SubWorkflowState
{
    // ── Inputs ───────────────────────────────────────────────────────────────────

    /// <summary>The brand/manufacturer site URL to crawl.</summary>
    public string SiteUrl { get; set; } = string.Empty;

    /// <summary>Display name of the brand (used as the product source + in timeline events).</summary>
    public string BrandName { get; set; } = string.Empty;

    /// <summary>The product category the crawler is hunting for in the catalogue (from the search query).</summary>
    public string Category { get; set; } = string.Empty;

    // ── Working state ────────────────────────────────────────────────────────────

    /// <summary>The LLM's read of where the brand's catalogue lives — the catalogue landing + matching product lines.</summary>
    public BrandCatalogAssessment Catalog { get; set; } = new();

    /// <summary>How many catalogue/line pages have been walked (bounded by <c>PipelineLimits.CrawlMaxPages</c>).</summary>
    public int PagesFetched { get; set; }

    /// <summary>Every product model discovered across the walked catalogue pages (deduped by URL/identity).</summary>
    public List<ProductListing> Discovered { get; init; } = new();

    // ── Outputs ──────────────────────────────────────────────────────────────────

    /// <summary>How many models were persisted (deep-dived + listing-level remainder).</summary>
    public int Persisted { get; set; }

    /// <summary>How many priced models were written to the <c>ScrapedPrice</c> series (usually 0 — brand sites rarely price).</summary>
    public int PricesRecorded { get; set; }
}
