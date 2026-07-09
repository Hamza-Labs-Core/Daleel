using Daleel.Agent;
using Daleel.Web.Data;

namespace Daleel.Web.Pipeline.Enrichment;

/// <summary>
/// Work-item kinds. One kind = one handler = one API dive per execution; anything bigger fans out
/// into more items (the queue is the recursion mechanism, never an in-process loop with a shared
/// lifetime).
/// </summary>
public static class EnrichmentUnit
{
    /// <summary>Job-level: cheap brand-DB fill inline, then fans out every other unit below.</summary>
    public const string Plan = "enrich.plan";

    /// <summary>One item: fresh-profile reuse or one official-page scrape + profile upsert.</summary>
    public const string ItemDive = "enrich.item";

    /// <summary>Job-level: vision identification (internally capped + budgeted).</summary>
    public const string Vision = "enrich.vision";

    /// <summary>One store domain: match drained edge prices first, inline catalogue crawl as fallback.</summary>
    public const string CatalogAttach = "enrich.catalog";

    /// <summary>
    /// One brand: FULL durable research — official site, Context.dev intelligence, social profiles,
    /// site catalogue — each step saved to the Brand row the moment it lands (a retry never repeats
    /// a completed step, and a brand researched by any search within 7 days is reused wholesale).
    /// </summary>
    public const string BrandResearch = "enrich.brandresearch";

    /// <summary>Job-level: paid image lookups for whatever is still imageless (deliberately late).</summary>
    public const string ImageLookup = "enrich.images";

    /// <summary>Job-level: classify-worker condition backfill (deliberately last).</summary>
    public const string Conditions = "enrich.conditions";

    /// <summary>
    /// Job-level DISPATCHER: finds offer/mention pages needing verification and enqueues one
    /// <see cref="VerifyPage"/> unit per page — one page, one unit, no batches, no chains.
    /// </summary>
    public const string PriceFetch = "enrich.prices";

    /// <summary>ONE page: fetch, judge relatedness (unrelated offers removed), price, condition
    /// truth, description, mention-offer creation and sibling-SKU discovery.</summary>
    public const string VerifyPage = "enrich.verifypage";

    /// <summary>Job-level: prunes offers whose sites users can't actually reach (deliberately last).</summary>
    public const string Reachability = "enrich.reachability";

    /// <summary>
    /// Job-level HALAL SAFETY: vision-screens the FINAL product/brand grid images and strips any judged
    /// haram (indecent/immodest, alcohol, pork…). Runs after image lookup/catalog/brand settle their
    /// images — the gather-stage moderation only saw the raw search results, not these enriched images.
    /// Always on (no flag): a missing vision model just no-ops.
    /// </summary>
    public const string ImageCheck = "enrich.imagecheck";

    /// <summary>Job-level: smart-cache gap refill (the ServeAndEnrich path) as one durable unit.</summary>
    public const string CacheGapRefill = "enrich.regaps";

    /// <summary>
    /// Job-level, settle-gated LAST unit: an LLM "makes sense of" the finished result with exactly
    /// three batched calls (one over all products, one over all brands, one for the search), each
    /// reduction written back to an existing summary field. Size-independent cost; deliberately runs
    /// after the world has settled (waits on the queue's OpenCount) so it reads the final grid.
    /// </summary>
    public const string Synthesize = "enrich.synthesize";
}

/// <summary>Payload of <see cref="EnrichmentUnit.Plan"/> and <see cref="EnrichmentUnit.CacheGapRefill"/>.</summary>
public sealed record PlanPayload(string? QualityJson = null, int FilteredCount = 0, string FilteredCategories = "");

/// <summary>One-item payload. Name double-checks the index — items move when vision renames models.</summary>
public sealed record ItemPayload(int Index, string Name);

public sealed record CatalogPayload(string Domain, string? StoreName, string? EntryUrl = null);

public sealed record BrandPayload(string Brand);

/// <summary>One page to verify + the models it was selected for (mention pages create offers).</summary>
public sealed record VerifyPagePayload(string Url, List<string> ModelNames, bool FromMention = false);

/// <summary>Names already attempted by prior image-lookup passes — so the chain advances, never loops.</summary>
public sealed record ImageLookupPayload(List<string> Attempted);

/// <summary>What a handler execution decided. Exactly one of these per attempt.</summary>
public abstract record UnitOutcome
{
    public sealed record Done : UnitOutcome;

    /// <summary>Try again later (backoff by attempts unless a delay is given). Reason is stored.</summary>
    public sealed record Retry(string Reason, TimeSpan? Delay = null) : UnitOutcome;

    /// <summary>Non-retryable give-up (cost cap, poison payload). Stored visibly on the dead row.</summary>
    public sealed record Kill(string Reason) : UnitOutcome;

    /// <summary>
    /// Park and retry WITHOUT consuming the attempt budget — the work is fine, the INFRA is down
    /// (billing 402, provider 5xx/429, timeout). The unit stays queued and re-runs until the outage
    /// clears; it NEVER dies. Distinct from <see cref="Retry"/> (which counts toward MaxAttempts and
    /// eventually Deads) so a long outage can't silently drop pending work.
    /// </summary>
    public sealed record Requeue(string Reason, TimeSpan? Delay = null) : UnitOutcome;

    public static readonly Done Ok = new();
}

/// <summary>
/// Per-execution context handed to handlers by the consumer: the claimed unit's job row, a lazily
/// built (metered) agent, the row-locked result store, and the queue for follow-up fan-out. The
/// service provider is the execution's own DI scope — resolve scoped services from it, never from
/// singletons (Blazor Server DbContext rules apply here too).
/// </summary>
public sealed class EnrichmentUnitContext
{
    public required IServiceProvider Services { get; init; }
    public required SearchJob Job { get; init; }

    /// <summary>Built on first use; wired to this execution's cost collector + ambient observer.</summary>
    public required Func<AgentService> Agent { get; init; }

    /// <summary>
    /// Builds an agent for a SPECIFIC model (same cost collector), or the job's model when null. The
    /// actor loops pin a capable model here — an agentic reason→act JSON loop can't run on the weak
    /// free-tier default. Optional so test contexts that only set <see cref="Agent"/> keep working.
    /// </summary>
    public Func<string?, AgentService>? AgentForModel { get; init; }

    public required IEnrichedResultStore Results { get; init; }
    public required IEnrichmentWorkQueue Queue { get; init; }
}

/// <summary>One unit kind's executor. Stateless — register as singleton, resolve deps per call.</summary>
public interface IEnrichmentUnitHandler
{
    string Kind { get; }

    Task<UnitOutcome> ExecuteAsync(EnrichmentWorkItem item, EnrichmentUnitContext ctx, CancellationToken ct);
}
