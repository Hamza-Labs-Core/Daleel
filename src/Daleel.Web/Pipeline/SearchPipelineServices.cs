using Daleel.Agent;
using Daleel.Core.Caching;
using Daleel.Web.Events;

namespace Daleel.Web.Pipeline;

/// <summary>
/// The live, NON-serializable half of a single search run — the request-scoped services and callbacks the
/// activities need but Elsa must never persist. Split out of <see cref="SearchPipelineState"/> so that the
/// state itself is pure, JSON-serializable data Elsa can persist/resume, while these references are resolved
/// fresh from DI on each activity (and re-resolved on a resume) instead of round-tripping through
/// serialized <c>WorkflowState</c>.
/// </summary>
/// <remarks>
/// Registered <c>Scoped</c> and seeded once per run by <see cref="Conversation.WorkflowSearchRunner"/>: the
/// run creates a DI scope, fills this with the job's agent + progress sink, and the Elsa activities resolve
/// this same instance from their execution context (<c>context.GetRequiredService&lt;SearchPipelineServices&gt;()</c>).
/// Holds only references owned elsewhere (the agent is built per request; the cache is a singleton), so it
/// owns no disposables of its own.
/// </remarks>
public sealed class SearchPipelineServices
{
    /// <summary>The request-scoped agent that drives planning, gathering, extraction and scraping.</summary>
    public AgentService Agent { get; set; } = default!;

    /// <summary>The result cache (PostgreSQL-backed). Null when caching is disabled for the run.</summary>
    public ICacheStore? Cache { get; set; }

    /// <summary>Progress sink the run streams live status through (SignalR → the conversation UI).</summary>
    public Action<string>? Progress { get; set; }

    /// <summary>Emits a plain, step-less progress line (used for non-localized/legacy text).</summary>
    public void Log(string message) => Progress?.Invoke(message);

    /// <summary>
    /// Emits a structured progress signal: advances the UI stepper to <paramref name="step"/> and supplies a
    /// localization <paramref name="key"/> (+ optional format args) the client resolves in the viewer's own
    /// culture. Encoded into the same string channel — see <see cref="SearchProgressSignal"/>. An arg of the
    /// form <c>$Some.Resource.Key</c> tells the client to localize that arg before formatting (used for the
    /// query-type noun, which is itself translatable).
    /// </summary>
    public void Report(SearchStep step, string key, params object?[] args) =>
        Progress?.Invoke(SearchProgressSignal.Encode(step, key, args));

    /// <summary>
    /// Pushes an INTERMEDIATE result (serialized answer JSON + result type) to the user's devices while
    /// the run is still going — the UI renders the grid immediately and results keep loading as they're
    /// ready. Seeded by the runner with the broadcaster's Partial channel; null in contexts with no live
    /// listener (tests, CLI). Best-effort: pushes must never fail or slow the pipeline.
    /// </summary>
    public Func<string, string, Task>? PushPartial { get; set; }

    /// <summary>
    /// Serializes the run's CURRENT answer state and pushes it as a partial result. Best-effort by
    /// design — a serialization or transport hiccup is superseded by the next push or the final
    /// Completed, never surfaced as a failure.
    /// </summary>
    public async Task TryPushPartialAsync(SearchPipelineState state)
    {
        if (PushPartial is null)
        {
            return;
        }

        try
        {
            // Lean payload: partials repeat every ~800ms and the partial UI renders only the products,
            // so the heavy research bundle (scraped pages, social posts) stays out of the push.
            if (Conversation.WorkflowSearchRunner.SalvageResultJson(state, includeResearch: false) is { } json)
            {
                await PushPartial(json, "ask").ConfigureAwait(false);
            }
        }
        catch
        {
            // Best-effort: a failed partial must never fault the search.
        }
    }
}
