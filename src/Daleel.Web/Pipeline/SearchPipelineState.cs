using Daleel.Agent;
using Daleel.Core.Caching;
using Daleel.Core.Geo;
using Daleel.Core.Intelligence;
using Daleel.Core.Models;
using Daleel.Web.Events;

namespace Daleel.Web.Pipeline;

/// <summary>
/// The mutable state that flows through the Elsa <see cref="SearchWorkflow"/>. Registered scoped and
/// shared by reference with the activities (Elsa runs them in the caller's DI scope), so the heavy
/// domain objects — the gathered bundle, the agent answer — never have to round-trip through Elsa's
/// serialized WorkflowState. The <see cref="WorkflowSearchRunner"/> seeds the inputs, runs the
/// workflow, then reads the outputs back off this same instance.
/// </summary>
/// <remarks>
/// ⚠️ INVARIANT — NEVER PERSIST OR SUSPEND THIS WORKFLOW. <see cref="Agent"/> is a live service and
/// <see cref="Progress"/> is a delegate; neither is serializable. This design works only because Elsa
/// is registered core-only with no persistence and the workflow runs to completion in one pass. The
/// moment an Elsa bookmark/Delay/suspend-resume or instance-store persistence is introduced, the
/// workflow would resume with a null <see cref="Agent"/>/<see cref="Progress"/> and silently emit empty
/// results with no error. Program.cs asserts no Elsa persistence module is registered to enforce this.
/// </remarks>
public sealed class SearchPipelineState
{
    // ── Inputs (seeded before the run) ───────────────────────────────────────────
    public AgentService Agent { get; set; } = default!;
    public string Query { get; set; } = string.Empty;
    public string Geo { get; set; } = "jordan";
    public string Language { get; set; } = "en";
    public string ResultKey { get; set; } = string.Empty;
    public ICacheStore? Cache { get; set; }
    public TimeSpan CacheTtl { get; set; } = TimeSpan.FromDays(30);
    public Action<string>? Progress { get; set; }

    /// <summary>Correlation id (the SearchJob id) stamped onto every recorded pipeline event.</summary>
    public string? SearchId { get; set; }

    /// <summary>
    /// Buffer of non-provider pipeline events (cache hits/misses, profile lookups) recorded by the
    /// activities. Provider calls are captured separately via the API-call collector. The runner
    /// flushes both to the event store at the end of the run.
    /// </summary>
    public List<PipelineEvent> Events { get; } = new();

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

    public bool IsProductQuery => Strategy?.QueryType == QueryType.ProductResearch;

    /// <summary>Emits a plain, step-less progress line (used for non-localized/legacy text).</summary>
    public void Log(string message) => Progress?.Invoke(message);

    /// <summary>
    /// Emits a structured progress signal: advances the UI stepper to <paramref name="step"/> and
    /// supplies a localization <paramref name="key"/> (+ optional format args) the client resolves in
    /// the viewer's own culture. Encoded into the same string channel — see <see cref="SearchProgressSignal"/>.
    /// An arg of the form <c>$Some.Resource.Key</c> tells the client to localize that arg before
    /// formatting (used for the query-type noun, which is itself translatable).
    /// </summary>
    public void Report(SearchStep step, string key, params object?[] args) =>
        Progress?.Invoke(SearchProgressSignal.Encode(step, key, args));

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
