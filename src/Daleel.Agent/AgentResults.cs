using Daleel.Core.Models;
using Daleel.Core.Observability;
using Daleel.Search.Abstractions;

namespace Daleel.Agent;

/// <summary>Tunables and ambient services for an <see cref="AgentService"/>.</summary>
public sealed class AgentOptions
{
    /// <summary>Default market when a query doesn't specify one.</summary>
    public string DefaultGeo { get; init; } = "usa";

    /// <summary>Max results to request per search query (places/social depth).</summary>
    public int ResultsPerQuery { get; init; } = 10;

    /// <summary>
    /// Depth for SHOPPING queries: the provider pages through Google Shopping until it reaches this
    /// many results (SerpAPI caps at 10 pages). Defaults to a deep 100-result scan — this feeds the
    /// price/Deals surface, where exhaustive priced coverage is the whole point. Total cost is still
    /// bounded by <see cref="MaxQueriesPerKind"/> and the per-job API-call cap.
    /// </summary>
    /// <remarks>
    /// WEB no longer uses this depth: SerpAPI is now a DISCOVERY layer (find brand/store/marketplace
    /// URLs, which brand/store enrichment then reads), so web pages shallow via
    /// <see cref="WebDiscoveryResultsPerQuery"/>. Keeping shopping deep leaves the Deals feature intact.
    /// </remarks>
    public int DeepResultsPerQuery { get; init; } = 100;

    /// <summary>
    /// Depth for WEB queries — deliberately shallow (≈1–2 SerpAPI pages). Web search is DISCOVERY-only:
    /// it surfaces the brand/store/marketplace URLs the pipeline then reads via CF-Browser (stores) and
    /// Context.dev (brands), so we don't page 10 deep. This is the single biggest lever on the SerpAPI
    /// hourly cap — 14 queries × ~2 pages instead of × 10. Bump to 30 (≈3 pages) if local-store discovery
    /// in thin markets regresses.
    /// </summary>
    public int WebDiscoveryResultsPerQuery { get; init; } = 20;

    /// <summary>Max web/shopping queries to actually execute from a plan (cost guard).</summary>
    /// <remarks>
    /// A broad query set is what gives "best &lt;category&gt;" searches their coverage — each diverse query
    /// (per-brand, per-tier, buying-guide, local-store-finder) surfaces a different slice of the catalogue.
    /// In local markets the store-discovery queries ("&lt;category&gt; online store &lt;country&gt;",
    /// "&lt;category&gt; متجر", "buy &lt;category&gt; &lt;city&gt;") are what surface small local e-commerce
    /// sites the global engines bury — so the planner is asked for 8–10 diverse queries and this cap keeps
    /// them. Kept generous; total cost stays bounded by the per-job API-call cap.
    /// </remarks>
    public int MaxQueriesPerKind { get; init; } = 14;

    /// <summary>
    /// Max URLs to deep-read per run. For research-style queries the buying-guide / round-up articles ARE the
    /// product source, so reading more of them directly widens how many distinct models the extractor can
    /// surface; bounded by the per-job API-call cap.
    /// </summary>
    public int MaxUrlsToRead { get; init; } = 6;

    /// <summary>
    /// Max store/marketplace pages to deep-extract priced listings from per run. Higher than
    /// <see cref="MaxUrlsToRead"/> because Google Shopping coverage is thin in many markets, so real
    /// prices have to be scraped from the store pages themselves — going deeper here is what turns
    /// "Price on site" into actual prices. Cost stays bounded by the per-job API-call cap.
    /// </summary>
    public int MaxListingUrls { get; init; } = 12;

    /// <summary>
    /// BCP-47 language the analyst summary should be written in (e.g. "ar", "en"). Product and
    /// brand names are left untranslated. Defaults to English.
    /// </summary>
    public string Language { get; init; } = "en";

    /// <summary>Clock, injectable for deterministic tests.</summary>
    public Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.UtcNow;

    /// <summary>Optional progress logger.</summary>
    public Action<string>? Log { get; init; }

    /// <summary>
    /// Optional live per-search event sink. When set, pipeline steps emit semantic <see cref="SearchEvent"/>s
    /// (discovery counts, scrape fallbacks, per-page extraction, …) that the Web layer persists to the
    /// timeline. Resolved by each emitter as <c>Events ?? AmbientSearchEvents.Sink</c>, so the ambient
    /// carrier covers DI-resolved collaborators too.
    /// </summary>
    public ISearchEventSink? Events { get; init; }

    /// <summary>Max in-flight per-page extraction LLM calls (fan-out concurrency). Bounds LLM rate-limit
    /// pressure without dropping any part.</summary>
    public int ExtractionConcurrency { get; init; } = 4;

    /// <summary>Max extraction parts (signals + N pages) per run — a worst-case wall-clock guard against the
    /// workflow deadline. 0 = uncapped.</summary>
    public int ExtractionMaxParts { get; init; } = 12;

    /// <summary>Max chars of a single scraped page fed to one extraction call — a generous cap so no single
    /// part can reintroduce the monolithic hang.</summary>
    public int ExtractionMaxPageChars { get; init; } = 40_000;

    /// <summary>How many listing PAGES to crawl per store (pagination): page 1 + up to depth-1 derived next
    /// pages, stop-on-empty. 1 = page 1 only (no pagination).</summary>
    public int StorePageDepth { get; init; } = 3;
}

/// <summary>
/// Everything the agent gathered while executing a strategy, before analysis. The
/// specialized report builders (brand/product/etc.) project from this bundle.
/// </summary>
public sealed record ResearchBundle
{
    public SearchStrategy Strategy { get; init; } = new();
    public IReadOnlyList<SearchResult> WebResults { get; init; } = Array.Empty<SearchResult>();
    public IReadOnlyList<SearchResult> ShoppingResults { get; init; } = Array.Empty<SearchResult>();
    public IReadOnlyList<StoreLocation> Stores { get; init; } = Array.Empty<StoreLocation>();
    public IReadOnlyList<SocialPost> SocialPosts { get; init; } = Array.Empty<SocialPost>();
    public IReadOnlyList<PricePoint> Prices { get; init; } = Array.Empty<PricePoint>();
    public IReadOnlyList<ScrapedPage> Pages { get; init; } = Array.Empty<ScrapedPage>();

    /// <summary>Distinct source URLs the bundle drew on.</summary>
    public IReadOnlyList<string> Sources { get; init; } = Array.Empty<string>();
}

/// <summary>The answer to a free-form <c>ask</c> query.</summary>
public sealed record AgentAnswer
{
    public string Question { get; init; } = string.Empty;
    public string Geo { get; init; } = string.Empty;
    public QueryType QueryType { get; init; }
    public string Summary { get; init; } = string.Empty;
    public ResearchBundle Research { get; init; } = new();

    /// <summary>
    /// Structured product listings, present only when the query was classified as a product
    /// search (e.g. "ACs in Jordan"). Drives the product-grid UI.
    /// </summary>
    public ProductSearchResult? Products { get; init; }

    public DateTimeOffset GeneratedAt { get; init; }
}
