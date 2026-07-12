using Daleel.Core.Observability;

namespace Daleel.Web.Events;

/// <summary>Builds a per-run <see cref="ISearchEventSink"/> stamped with the run's correlation id + user hash.</summary>
public interface ISearchEventSinkFactory
{
    ISearchEventSink For(string correlationId, string? userHash);
}

/// <summary>
/// Maps a layer-neutral <see cref="SearchEvent"/> (from Daleel.Core, emitted anywhere in the pipeline) to a
/// persisted <see cref="SystemEvent"/> stamped with the current run's correlation id + user hash, and hands
/// it to the <see cref="SystemEventWriter"/> — no DB work on the hot path. One instance per run.
///
/// It ALSO mirrors every event through <see cref="ILogger"/> at the matching level. The pipeline (discovery,
/// extraction, failover) reports progress ONLY through this semantic-event channel — it never calls ILogger
/// directly — so without this bridge those events reached the admin timeline but NOT Serilog, and therefore
/// never landed in the file/R2 log sinks (only Elsa's own ILogger lines did). Logging here puts the whole
/// pipeline trail on the same Serilog pipeline as everything else, under a stable, greppable source context.
/// </summary>
public sealed class SystemEventSink : ISearchEventSink
{
    private readonly SystemEventWriter _writer;
    private readonly ILogger _logger;
    private readonly string _correlationId;
    private readonly string? _userHash;

    public SystemEventSink(SystemEventWriter writer, ILogger logger, string correlationId, string? userHash)
        => (_writer, _logger, _correlationId, _userHash) = (writer, logger, correlationId, userHash);

    public void Emit(SearchEvent ev)
    {
        var category = string.IsNullOrWhiteSpace(ev.Category) ? SystemEventCategory.Workflow : ev.Category;
        var source = string.IsNullOrWhiteSpace(ev.Source) ? "pipeline" : ev.Source;

        _writer.Enqueue(new SystemEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            Category = category,
            EventType = ev.EventType,
            Severity = ev.Level switch
            {
                SearchEventLevel.Error => SystemEventSeverity.Error,
                SearchEventLevel.Warning => SystemEventSeverity.Warning,
                _ => SystemEventSeverity.Info
            },
            Source = source,
            Summary = ev.Summary,
            DetailsJson = SerializeDetails(ev.Details),
            CorrelationId = _correlationId,
            UserHash = _userHash
        });

        // Mirror to Serilog (Console + File + R2). Info-level events are the pipeline's normal narrative —
        // discovery counts, extraction yields, failovers — and the prod minimum level is Information, so
        // they must log at Information (not Debug) to reach the sinks. Structured properties keep each line
        // greppable by run and stage. The message itself is the already-human-readable event summary.
        var level = ev.Level switch
        {
            SearchEventLevel.Error => LogLevel.Error,
            SearchEventLevel.Warning => LogLevel.Warning,
            _ => LogLevel.Information
        };
        _logger.Log(
            level,
            "[{Category}/{EventType}] {Summary} (source {Source}, run {CorrelationId})",
            category, ev.EventType, ev.Summary, source, _correlationId);
    }

    private static string SerializeDetails(IReadOnlyDictionary<string, object?>? details) =>
        details is { Count: > 0 } ? System.Text.Json.JsonSerializer.Serialize(details) : "{}";
}

/// <summary>Singleton factory closing over the singleton writer (no DbContext capture — safe to hold).</summary>
public sealed class SystemEventSinkFactory : ISearchEventSinkFactory
{
    private readonly SystemEventWriter _writer;
    private readonly ILogger _logger;

    public SystemEventSinkFactory(SystemEventWriter writer, ILoggerFactory loggerFactory)
    {
        _writer = writer;
        // A dedicated, stable source context so operators can grep the whole pipeline trail in one filter
        // (and it is emitted at Information, so it survives the prod minimum level and reaches the R2 sink).
        _logger = loggerFactory.CreateLogger("Daleel.Pipeline");
    }

    public ISearchEventSink For(string correlationId, string? userHash) =>
        new SystemEventSink(_writer, _logger, correlationId, userHash);
}
