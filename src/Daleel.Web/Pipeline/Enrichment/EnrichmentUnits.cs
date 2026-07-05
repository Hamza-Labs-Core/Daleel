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

    /// <summary>One brand: harvest its site into the BrandModel DB + refill the result from it.</summary>
    public const string BrandHarvest = "enrich.brand";

    /// <summary>Job-level: paid image lookups for whatever is still imageless (deliberately late).</summary>
    public const string ImageLookup = "enrich.images";

    /// <summary>Job-level: classify-worker condition backfill (deliberately last).</summary>
    public const string Conditions = "enrich.conditions";

    /// <summary>Job-level: prunes offers whose sites users can't actually reach (deliberately last).</summary>
    public const string Reachability = "enrich.reachability";

    /// <summary>Job-level: smart-cache gap refill (the ServeAndEnrich path) as one durable unit.</summary>
    public const string CacheGapRefill = "enrich.regaps";
}

/// <summary>Payload of <see cref="EnrichmentUnit.Plan"/> and <see cref="EnrichmentUnit.CacheGapRefill"/>.</summary>
public sealed record PlanPayload(string? QualityJson = null, int FilteredCount = 0, string FilteredCategories = "");

/// <summary>One-item payload. Name double-checks the index — items move when vision renames models.</summary>
public sealed record ItemPayload(int Index, string Name);

public sealed record CatalogPayload(string Domain, string? StoreName);

public sealed record BrandPayload(string Brand);

/// <summary>What a handler execution decided. Exactly one of these per attempt.</summary>
public abstract record UnitOutcome
{
    public sealed record Done : UnitOutcome;

    /// <summary>Try again later (backoff by attempts unless a delay is given). Reason is stored.</summary>
    public sealed record Retry(string Reason, TimeSpan? Delay = null) : UnitOutcome;

    /// <summary>Non-retryable give-up (cost cap, poison payload). Stored visibly on the dead row.</summary>
    public sealed record Kill(string Reason) : UnitOutcome;

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

    public required IEnrichedResultStore Results { get; init; }
    public required IEnrichmentWorkQueue Queue { get; init; }
}

/// <summary>One unit kind's executor. Stateless — register as singleton, resolve deps per call.</summary>
public interface IEnrichmentUnitHandler
{
    string Kind { get; }

    /// <summary>
    /// Wall-clock budget for ONE attempt of this unit. Expiry retries THIS unit only — there is no
    /// phase- or job-level enrichment timeout anywhere, by design.
    /// </summary>
    TimeSpan Budget { get; }

    Task<UnitOutcome> ExecuteAsync(EnrichmentWorkItem item, EnrichmentUnitContext ctx, CancellationToken ct);
}
