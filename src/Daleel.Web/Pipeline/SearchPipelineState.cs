using System.Text.Json.Serialization;
using Daleel.Agent;
using Daleel.Core.Geo;
using Daleel.Core.Intelligence;
using Daleel.Core.Models;
using Daleel.Web.Events;

namespace Daleel.Web.Pipeline;

/// <summary>
/// The run state that flows through the Elsa <see cref="SearchWorkflow"/>: the query inputs, the
/// intermediate domain objects each activity fills in, and the outputs the runner reads back. Every member
/// is plain data (primitives, strings, records, lists) — no services, delegates or other live references.
/// The live, non-serializable half (the agent, the cache, the progress sink) lives in
/// <see cref="SearchPipelineServices"/>, resolved separately from DI by each activity.
/// </summary>
/// <remarks>
/// Registered <c>Scoped</c>; <see cref="Conversation.WorkflowSearchRunner"/> seeds the inputs on a per-run
/// instance, runs the workflow to completion in a single in-process pass, then reads the outputs back off it.
/// <para>
/// This state lives in the DI scope, NOT in Elsa's <c>WorkflowState</c>/<c>Variables</c>, so the workflow is
/// <strong>not</strong> resumable across a real suspend/bookmark: a resume would build a fresh, blank instance
/// from a new DI scope and silently emit empty results. Keep every activity synchronous (no Delay/bookmarks).
/// Making this type plain-data buys one thing — registering Elsa's instance store no longer trips its
/// state-serialization guard, so the runner can persist a <em>completed-run summary</em>
/// (<see cref="WorkflowRunSummary"/>) for the admin workflows page. That is persistence of finished runs, not
/// mid-run suspend/resume.
/// </para>
/// </remarks>
public sealed class SearchPipelineState
{
    // ── Inputs (seeded before the run) ───────────────────────────────────────────
    public string Query { get; set; } = string.Empty;
    public string Geo { get; set; } = "jordan";
    public string Language { get; set; } = "en";
    public string ResultKey { get; set; } = string.Empty;
    // 24h, not 30 days: this caches product/price/availability data, which goes stale fast, and a long
    // TTL lets any bad entry (a transient empty, a since-fixed bug) linger for far too long. Quality
    // re-validation on read (CheckCacheActivity) and the empty-skip on write keep even this window honest.
    public TimeSpan CacheTtl { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Correlation id (the SearchJob id) stamped onto every recorded pipeline event.</summary>
    public string? SearchId { get; set; }

    // ── Timing (for the persisted instance + admin workflows page) ───────────────
    /// <summary>When the run was seeded — the workflow's effective start.</summary>
    public DateTimeOffset? StartedAt { get; set; }

    /// <summary>When the run finished assembling its result (set by the terminal activity).</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>
    /// Buffer of non-provider pipeline events (cache hits/misses, profile lookups) recorded by the
    /// activities. Provider calls are captured separately via the API-call collector. The runner
    /// flushes both to the event store at the end of the run.
    /// </summary>
    public List<PipelineEvent> Events { get; init; } = new();

    // ── Intermediate results (filled by activities) ──────────────────────────────
    public GeoProfile? GeoProfile { get; set; }
    public SearchStrategy? Strategy { get; set; }

    /// <summary>
    /// What KIND of thing the user wants — product, service, or place — as classified by the planner
    /// (copied off <see cref="Strategy"/> in <c>ParseQueryActivity</c>). Drives which extraction prompt
    /// the agent runs. Defaults to <see cref="SearchIntentType.Product"/> so an unclassified run behaves
    /// exactly as before.
    /// </summary>
    public SearchIntentType Intent { get; set; } = SearchIntentType.Product;

    /// <summary>
    /// The up-front category "thinking" produced by <c>AnalyzeMarketActivity</c> before sources are
    /// gathered: product type, relevant store types, expected brands, the comparison schema and a
    /// price expectation. Threaded into extraction (schema-aware) and the final result. Null for
    /// non-product queries or when the analysis is skipped/fails.
    /// </summary>
    public SearchIntelligence? Intelligence { get; set; }

    public ResearchBundle? Bundle { get; set; }
    public string Summary { get; set; } = string.Empty;
    public ProductSearchResult? Products { get; set; }
    public AgentAnswer? Answer { get; set; }

    // ── Outputs (read by the runner) ─────────────────────────────────────────────

    /// <summary>True when CheckCache served a stored report; downstream activities then no-op.</summary>
    public bool FromCache { get; set; }

    /// <summary>
    /// The completeness verdict on a served cache hit (null on a fresh run or a quality-rejected hit
    /// that ran live). When <see cref="FromCache"/> is true, the runner surfaces this on the run result
    /// so a <see cref="CacheDecision.ServeAndEnrich"/> hit triggers a background partial re-enrichment.
    /// </summary>
    [JsonIgnore]
    public CacheQualityReport? CacheQuality { get; set; }
    public string ResultJson { get; set; } = string.Empty;
    public string ResultType { get; set; } = "ask";
    public int FilteredCount { get; set; }
    public string FilteredCategories { get; set; } = string.Empty;
    public int ResultCount { get; set; }

    [JsonIgnore]
    public bool IsProductQuery => Strategy?.QueryType == QueryType.ProductResearch;

    /// <summary>Buffers a categorized pipeline event (cache/profile/…) for the event store.</summary>
    public void RecordEvent(
        string category, string eventType, string provider,
        bool success = true, decimal cost = 0m, long durationMs = 0,
        IReadOnlyDictionary<string, object?>? metadata = null) =>
        Events.Add(PipelineEventFactory.Custom(
            category, eventType, provider, SearchId, success, cost, durationMs, metadata));
}

/// <summary>
/// The cached shape of a completed search — mirrors the record the legacy AgentSearchRunner wrote,
/// so the workflow's CheckCache/CacheResults steps read and write cache entries that remain
/// compatible (the same JSON fields).
/// </summary>
public sealed record CachedSearchResult(
    string ResultJson, string ResultType, int FilteredCount = 0, string FilteredCategories = "");
