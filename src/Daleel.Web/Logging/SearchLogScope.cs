namespace Daleel.Web.Logging;

/// <summary>
/// Per-search logging scope: opened once at each execution entry point (the search-job worker, each
/// enrichment-queue unit, the drain, the re-enrich repair pass), it stamps <c>SearchJobId</c> — and,
/// for queue work, <c>UnitKind</c> — as structured properties on EVERY ILogger line emitted beneath
/// it, including third-party code on the same async flow (Elsa, EF, the agent). Serilog's JSON
/// file/R2 sinks serialize scope properties, so one grep for <c>"SearchJobId":46</c> reconstructs a
/// search's entire flow across all components. The semantic-event channel (SystemEventSink) already
/// stamps its correlation id; this closes the gap for every direct ILogger call.
/// </summary>
public static class SearchLogScope
{
    public static IDisposable Begin(ILogger? logger, int searchJobId, string? unitKind = null)
    {
        if (logger is null)
        {
            return NoopScope.Instance;
        }

        // KeyValuePair-shaped state is what Serilog's MEL provider lifts into log-event properties.
        var state = unitKind is null
            ? new List<KeyValuePair<string, object?>>(1) { new("SearchJobId", searchJobId) }
            : new List<KeyValuePair<string, object?>>(2)
            {
                new("SearchJobId", searchJobId), new("UnitKind", unitKind)
            };
        return logger.BeginScope(state) ?? NoopScope.Instance;
    }

    private sealed class NoopScope : IDisposable
    {
        public static readonly NoopScope Instance = new();
        public void Dispose() { }
    }
}
