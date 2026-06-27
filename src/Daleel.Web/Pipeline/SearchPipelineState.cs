using System.Text.Json.Serialization;
using Daleel.Agent;
using Daleel.Core.Geo;
using Daleel.Core.Intelligence;
using Daleel.Core.Models;
using Daleel.Web.Events;

namespace Daleel.Web.Pipeline;

/// <summary>
/// The serializable state that flows through the Elsa <see cref="SearchWorkflow"/>: the query inputs, the
/// intermediate domain objects each activity fills in, and the outputs the runner reads back. Every member
/// is plain data (primitives, strings, records, lists) — no services, delegates or other live references —
/// so Elsa can persist and resume it. The live, non-serializable half (the agent, the cache, the progress
/// sink) lives in <see cref="SearchPipelineServices"/>, resolved separately from DI by each activity.
/// </summary>
/// <remarks>
/// Registered <c>Scoped</c>; <see cref="Conversation.WorkflowSearchRunner"/> seeds the inputs on a per-run
/// instance, runs the workflow, then reads the outputs back off it. Because nothing here is a live
/// reference, the workflow is safe to persist/suspend — the runner additionally hands a snapshot of this
/// state to Elsa's workflow-instance store so the admin workflows page can replay completed runs.
/// </remarks>
public sealed class SearchPipelineState
{
    // ── Inputs (seeded before the run) ───────────────────────────────────────────
    public string Query { get; set; } = string.Empty;
    public string Geo { get; set; } = "jordan";
    public string Language { get; set; } = "en";
    public string ResultKey { get; set; } = string.Empty;
    public TimeSpan CacheTtl { get; set; } = TimeSpan.FromDays(30);

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
