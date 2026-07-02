namespace Daleel.Core.Observability;

/// <summary>
/// Ambient (AsyncLocal) carrier for the CURRENT job's API-call observer + cost estimator.
///
/// The AgentFactory wires the observer into the LLM/search/scrape clients it builds per request —
/// but DI-resolved components (the product identifier's vision matcher, the brand-catalogue
/// searcher/harvester, the store-catalogue crawls) make paid calls on their own HTTP clients and
/// used to bypass the per-job metering entirely: real spend that never reached the usage dashboard,
/// the per-job cost estimate, or the cost cap. Those call sites now read the ambient observer.
///
/// AsyncLocal flows down through awaits, <c>Task.WhenAll</c> fan-outs, and DI-scope creation on the
/// same async flow, so one <see cref="Begin"/> at the top of a run/enrichment covers every paid call
/// underneath it — including sub-workflow children — with no plumbing through signatures. Parallel
/// jobs are separate async flows, so their scopes never bleed into each other. An empty ambient
/// (tests, admin tools, startup work) simply means "unmetered context": <see cref="ApiCallTimer"/>
/// no-ops on a null observer.
/// </summary>
public static class AmbientApiObserver
{
    private sealed record Ambient(IApiCallObserver Observer, CostEstimator Estimator);

    private static readonly AsyncLocal<Ambient?> Current = new();

    /// <summary>The current flow's observer, or null when no job scope is active.</summary>
    public static IApiCallObserver? Observer => Current.Value?.Observer;

    /// <summary>The current flow's cost estimator, or null when no job scope is active.</summary>
    public static CostEstimator? Estimator => Current.Value?.Estimator;

    /// <summary>
    /// Establishes the observer for the current async flow; dispose to restore the previous value
    /// (typically null). Use as <c>using var _ = AmbientApiObserver.Begin(collector, estimator);</c>.
    /// </summary>
    public static IDisposable Begin(IApiCallObserver observer, CostEstimator estimator)
    {
        var previous = Current.Value;
        Current.Value = new Ambient(observer, estimator);
        return new Restorer(previous);
    }

    private sealed class Restorer : IDisposable
    {
        private readonly Ambient? _previous;
        public Restorer(Ambient? previous) => _previous = previous;
        public void Dispose() => Current.Value = _previous;
    }
}
