using Daleel.Agent;
using Daleel.Web.Pipeline;

namespace Daleel.Web.Pipeline.SubWorkflows;

/// <summary>
/// The live, NON-serializable half of a per-entity sub-workflow run (brand / store / item) — the agent and
/// progress sink the parent shares down. Split out of <see cref="SubWorkflowState"/> for the same reason as
/// <see cref="SearchPipelineServices"/>: the state stays pure serializable data, while these references are
/// resolved from each child's DI scope and never round-trip through Elsa's serialized state.
/// </summary>
/// <remarks>
/// Registered <c>Scoped</c>, so each dispatched child (which runs in its own DI scope) gets a fresh instance
/// that <see cref="SubWorkflowDispatcher"/> seeds from the parent run before the child workflow executes.
/// </remarks>
public sealed class SubWorkflowServices
{
    /// <summary>The request-scoped agent (page scraping for item deep-dives); shared from the parent run.</summary>
    public AgentService Agent { get; set; } = default!;

    /// <summary>Progress sink shared from the parent run so sub-workflow steps stream live status.</summary>
    public Action<string>? Progress { get; set; }

    /// <summary>Emits a plain (non-localized/legacy) progress line through the shared sink.</summary>
    public void Log(string message) => Progress?.Invoke(message);

    /// <summary>
    /// Emits a structured progress signal — a localization <paramref name="key"/> (+ optional format args)
    /// the client resolves in the viewer's own culture, tagged with the <paramref name="step"/> this
    /// sub-workflow runs within (brand → BuildingProfiles, store → FindingStores, item → ComparingPrices).
    /// Mirrors <see cref="SearchPipelineServices.Report"/> so per-entity child workflows stream the same
    /// culture-aware feed as the parent instead of hardcoded English. See <see cref="SearchProgressSignal"/>.
    /// </summary>
    public void Report(SearchStep step, string key, params object?[] args) =>
        Progress?.Invoke(SearchProgressSignal.Encode(step, key, args));
}
