namespace Daleel.Web.Pipeline;

/// <summary>
/// The pipeline's fan-out limits. Entity fan-outs are UNCAPPED by default: every discovered brand,
/// store and model is dispatched — dropping work on the floor was a VPS-era self-protection that the
/// Cloudflare execution layer (docs/architecture/cloudflare-workers-pipeline.md) makes obsolete. The
/// real backstops are the ones designed for the job: the workflow deadline + salvage, the per-unit
/// lease + queue retry budgets, and the per-(entity) freshness gates — all of which keep partial
/// results instead of silently truncating them. (Cost is metered + charged to credits, never used to
/// cap or cancel a running job — see R1 in <c>EnrichmentQueueService</c>.)
/// </summary>
/// <remarks>
/// Environment knobs (rendered into .env by the deploy workflows) exist as optional RESTRAINTS for an
/// environment that needs them, not as things to raise: <c>PIPELINE_SUBWORKFLOW_CONCURRENCY</c>,
/// <c>PIPELINE_MAX_BRANDS</c>, <c>PIPELINE_MAX_STORES</c>, <c>PIPELINE_MAX_ITEMS</c>. Values are read
/// once at startup (static init) — process-wide tuning, not per-request settings.
/// </remarks>
public static class PipelineLimits
{
    /// <summary>
    /// How many child sub-workflows run at once per dispatch step. This is a throughput WIDTH, not a
    /// result cap — nothing is dropped, work just queues for a slot. Bounds the VPS's own DB/HTTP
    /// pressure (every child opens a scope + connections on this box even when the heavy call runs
    /// on the edge).
    /// </summary>
    public static int SubWorkflowConcurrency { get; } =
        FromEnv("PIPELINE_SUBWORKFLOW_CONCURRENCY", fallback: 16);

    /// <summary>Brands researched per search — every discovered brand, unless an env restraint is set.</summary>
    public static int MaxBrands { get; } = FromEnv("PIPELINE_MAX_BRANDS", fallback: int.MaxValue);

    /// <summary>
    /// Brand catalogues harvested from Context.dev per search — every surfaced brand (the canonical
    /// brand-product source), unless an env restraint is set. UNCAPPED by default like the other
    /// fan-outs; the brand set a grid surfaces is naturally small and the per-(brand,level) TTL gate
    /// stops repeat searches re-billing. It gets its OWN restraint (<c>PIPELINE_MAX_BRAND_CATALOGS</c>)
    /// only because each harvest is a paid <c>/v1/brand/ai/products</c> crawl — so an operator can
    /// throttle the priciest fan-out independently, without also restraining brand research. (Note the
    /// per-job cost cap is not currently armed for ANY fan-out, so it is not a real backstop here.)
    /// </summary>
    public static int MaxBrandCatalogs { get; } = FromEnv("PIPELINE_MAX_BRAND_CATALOGS", fallback: int.MaxValue);

    /// <summary>Stores researched per search — every discovered store, unless an env restraint is set.</summary>
    public static int MaxStores { get; } = FromEnv("PIPELINE_MAX_STORES", fallback: int.MaxValue);

    /// <summary>Models deep-dived per search — every discovered model, unless an env restraint is set.</summary>
    public static int MaxItems { get; } = FromEnv("PIPELINE_MAX_ITEMS", fallback: int.MaxValue);

    /// <summary>
    /// Search jobs processed concurrently by one app instance — UNCAPPED by default (no job waits
    /// because of a count; each job is individually bounded by its own deadline + cost cap).
    /// Restrainable via <c>PIPELINE_MAX_CONCURRENT_JOBS</c>.
    /// </summary>
    public static int ConcurrentJobs { get; } = FromEnv("PIPELINE_MAX_CONCURRENT_JOBS", fallback: int.MaxValue);

    /// <summary>
    /// Enrichment work items executed concurrently by one app instance. A throughput WIDTH like
    /// <see cref="SubWorkflowConcurrency"/> — nothing is dropped, units just wait for a claim slot;
    /// the default bounds this box's own scrape/DB pressure while the queue drains at its own pace.
    /// Restrainable/widen-able via <c>PIPELINE_ENRICH_CONCURRENCY</c>. Units are independent by
    /// design (direct URLs, per-unit retry ledger, patches compose under the job row lock), so the
    /// width is limited by external courtesy, not correctness: 16 drains a 30-unit verifypage tail
    /// (~60s each) in ~2 min instead of ~5, without bursting any single vendor unreasonably —
    /// CF Browser does the fetching, and units within a run spread across different store domains.
    /// </summary>
    public static int EnrichmentConcurrency { get; } = FromEnv("PIPELINE_ENRICH_CONCURRENCY", fallback: 16);

    /// <summary>
    /// How many listing pages the LLM site crawler will walk per site before stopping — the pagination
    /// safety ceiling (the crawler also stops the moment the LLM reports no next page). Restrainable via
    /// <c>PIPELINE_CRAWL_MAX_PAGES</c>.
    /// </summary>
    public static int CrawlMaxPages { get; } = FromEnv("PIPELINE_CRAWL_MAX_PAGES", fallback: 25);

    /// <summary>
    /// How many discovered products the crawler deep-dives (visits the detail page + LLM-extracts full
    /// details) per site — UNCAPPED by default like every result fan-out (a numeric cap silently
    /// dropped products 13+ of every site). The real bound is the store sub-workflow timeout: dives
    /// run until time is up and everything else persists at listing level, so nothing is lost —
    /// merely less enriched. Restrainable via <c>PIPELINE_CRAWL_MAX_DEEPDIVE</c>.
    /// </summary>
    public static int CrawlMaxDeepDive { get; } = FromEnv("PIPELINE_CRAWL_MAX_DEEPDIVE", fallback: int.MaxValue);

    /// <summary>
    /// How many product detail pages the crawler deep-dives at once — a throughput WIDTH bounding this
    /// box's scrape/LLM pressure. Restrainable via <c>PIPELINE_CRAWL_CONCURRENCY</c>.
    /// </summary>
    public static int CrawlConcurrency { get; } = FromEnv("PIPELINE_CRAWL_CONCURRENCY", fallback: 8);

    private static int FromEnv(string name, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, out var parsed) && parsed > 0 ? parsed : fallback;
    }
}
