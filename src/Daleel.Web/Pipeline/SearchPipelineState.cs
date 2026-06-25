using Daleel.Agent;
using Daleel.Core.Caching;
using Daleel.Core.Geo;
using Daleel.Core.Models;

namespace Daleel.Web.Pipeline;

/// <summary>
/// The mutable state that flows through the Elsa <see cref="SearchWorkflow"/>. Registered scoped and
/// shared by reference with the activities (Elsa runs them in the caller's DI scope), so the heavy
/// domain objects — the gathered bundle, the agent answer — never have to round-trip through Elsa's
/// serialized WorkflowState. The <see cref="WorkflowSearchRunner"/> seeds the inputs, runs the
/// workflow, then reads the outputs back off this same instance.
/// </summary>
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

    // ── Intermediate results (filled by activities) ──────────────────────────────
    public GeoProfile? GeoProfile { get; set; }
    public SearchStrategy? Strategy { get; set; }
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

    public void Log(string message) => Progress?.Invoke(message);
}

/// <summary>
/// The cached shape of a completed search — mirrors the record the legacy AgentSearchRunner wrote,
/// so the workflow's CheckCache/CacheResults steps read and write cache entries that remain
/// compatible (the same JSON fields).
/// </summary>
public sealed record CachedSearchResult(
    string ResultJson, string ResultType, int FilteredCount = 0, string FilteredCategories = "");
