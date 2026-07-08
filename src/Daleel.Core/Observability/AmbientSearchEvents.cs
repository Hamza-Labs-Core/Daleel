namespace Daleel.Core.Observability;

/// <summary>
/// Ambient (AsyncLocal) carrier for the CURRENT search's live event sink — the semantic-event twin of
/// <see cref="AmbientApiObserver"/>.
///
/// One <see cref="Begin"/> at the top of a run/enrichment reaches AgentService, both routers, every
/// sub-workflow child scope and the background enrichment queue: AsyncLocal flows down through awaits,
/// <c>Task.WhenAll</c> fan-outs and DI-scope creation on the same async flow, so any step can
/// <see cref="ISearchEventSink.Emit"/> a <see cref="SearchEvent"/> with no plumbing through signatures.
/// Parallel searches are separate async flows, so their sinks never bleed into each other. An empty
/// ambient (tests, CLI, startup) simply means "no live sink" — emitters null-check <see cref="Sink"/>.
/// </summary>
public static class AmbientSearchEvents
{
    private static readonly AsyncLocal<ISearchEventSink?> Current = new();

    /// <summary>The current flow's live event sink, or null when no search scope is active.</summary>
    public static ISearchEventSink? Sink => Current.Value;

    /// <summary>
    /// Establishes the sink for the current async flow; dispose to restore the previous value (typically
    /// null). Use as <c>using var _ = AmbientSearchEvents.Begin(sink);</c>.
    /// </summary>
    public static IDisposable Begin(ISearchEventSink sink)
    {
        var previous = Current.Value;
        Current.Value = sink;
        return new Restorer(previous);
    }

    private sealed class Restorer : IDisposable
    {
        private readonly ISearchEventSink? _previous;
        public Restorer(ISearchEventSink? previous) => _previous = previous;
        public void Dispose() => Current.Value = _previous;
    }
}
