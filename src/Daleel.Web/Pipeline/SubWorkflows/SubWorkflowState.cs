using Daleel.Web.Events;

namespace Daleel.Web.Pipeline.SubWorkflows;

/// <summary>
/// Shared base for the per-entity sub-workflow states (brand / store / item). Each sub-workflow runs in
/// its OWN child DI scope, so it resolves its own scoped state instance — and, crucially, its own
/// <c>DaleelDbContext</c>, which is what lets the dispatchers fan the sub-workflows out in parallel
/// without the concurrency hazard the old sequential enrichment loop had to avoid.
///
/// Like <see cref="SearchPipelineState"/>, this holds only serializable data: the live agent + progress
/// sink moved to <see cref="SubWorkflowServices"/>, resolved from DI alongside the state. The dispatcher
/// reads the finished <c>Result</c> + <c>Events</c> back off the instance after the child workflow
/// completes and merges the events into the parent run's buffer.
/// </summary>
public abstract class SubWorkflowState : ISearchScopedState
{
    /// <summary>Market key (e.g. "jordan") the parent search resolved.</summary>
    public string Geo { get; set; } = "jordan";

    /// <summary>Correlation id (the SearchJob id) stamped onto every recorded event.</summary>
    public string? SearchId { get; set; }

    /// <summary>Events buffered by this sub-workflow's activities; merged into the parent run's buffer.</summary>
    public List<PipelineEvent> Events { get; init; } = new();

    /// <summary>Buffers a categorized pipeline event (profile/extract/places/…) for the event store.</summary>
    public void RecordEvent(
        string category, string eventType, string provider,
        bool success = true, decimal cost = 0m, long durationMs = 0,
        IReadOnlyDictionary<string, object?>? metadata = null) =>
        Events.Add(PipelineEventFactory.Custom(
            category, eventType, provider, SearchId, success, cost, durationMs, metadata));
}
