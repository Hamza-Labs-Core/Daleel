using Daleel.Core.Models;

namespace Daleel.Web.Pipeline.SubWorkflows;

/// <summary>
/// Per-store state for <see cref="StoreResearchWorkflow"/>. Seeded with the extracted
/// <see cref="StoreInfo"/>; the activities scrape the store's site, cross-reference Google Maps for
/// location/rating, extract contact info, persist the verified profile, and harvest catalogue prices —
/// folding everything back onto <see cref="Result"/>.
/// </summary>
public sealed class StoreResearchState : SubWorkflowState
{
    /// <summary>The store as extracted from the search (input).</summary>
    public StoreInfo Store { get; set; } = default!;

    /// <summary>The shopper's original search query — threaded down so the LLM site-crawl can navigate toward it.</summary>
    public string Query { get; set; } = string.Empty;

    /// <summary>
    /// True once the LLM site-crawl (<see cref="StoreCrawlWorkflow"/>) has harvested this store's catalogue —
    /// the intelligent replacement for the single-page fetch. When set, <see cref="ScrapePricesActivity"/>
    /// skips its now-redundant single-page catalogue call; when the crawl is unavailable it stays false and
    /// the single-page fetch runs as the fallback.
    /// </summary>
    public bool CrawledSite { get; set; }

    /// <summary>The enriched store (output). Starts equal to <see cref="Store"/>; folds in verified fields.</summary>
    public StoreInfo Result { get; set; } = default!;

    /// <summary>The saved profile found in the DB (may be stale).</summary>
    public Data.Store? Existing { get; set; }

    /// <summary>A freshly-researched profile (null when the saved one was fresh, or research was unavailable).</summary>
    public Data.Store? Researched { get; set; }

    /// <summary>The profile that won (researched ?? existing) — the one folded onto the result.</summary>
    public Data.Store? Saved { get; set; }

    /// <summary>True when the saved profile was fresh, so the network research step is skipped.</summary>
    public bool ResolvedFromCache { get; set; }

    /// <summary>How many priced products the store's catalogue yielded (Context.dev /v1/brand/ai/products).</summary>
    public int PricedProducts { get; set; }
}
