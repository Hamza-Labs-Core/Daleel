using Daleel.Agent;

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

    /// <summary>Emits a plain progress line through the shared sink.</summary>
    public void Log(string message) => Progress?.Invoke(message);
}
