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
/// </summary>
public sealed class SystemEventSink : ISearchEventSink
{
    private readonly SystemEventWriter _writer;
    private readonly string _correlationId;
    private readonly string? _userHash;

    public SystemEventSink(SystemEventWriter writer, string correlationId, string? userHash)
        => (_writer, _correlationId, _userHash) = (writer, correlationId, userHash);

    public void Emit(SearchEvent ev)
    {
        _writer.Enqueue(new SystemEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            Category = string.IsNullOrWhiteSpace(ev.Category) ? SystemEventCategory.Workflow : ev.Category,
            EventType = ev.EventType,
            Severity = ev.Level switch
            {
                SearchEventLevel.Error => SystemEventSeverity.Error,
                SearchEventLevel.Warning => SystemEventSeverity.Warning,
                _ => SystemEventSeverity.Info
            },
            Source = string.IsNullOrWhiteSpace(ev.Source) ? "pipeline" : ev.Source,
            Summary = ev.Summary,
            DetailsJson = SerializeDetails(ev.Details),
            CorrelationId = _correlationId,
            UserHash = _userHash
        });
    }

    private static string SerializeDetails(IReadOnlyDictionary<string, object?>? details) =>
        details is { Count: > 0 } ? System.Text.Json.JsonSerializer.Serialize(details) : "{}";
}

/// <summary>Singleton factory closing over the singleton writer (no DbContext capture — safe to hold).</summary>
public sealed class SystemEventSinkFactory : ISearchEventSinkFactory
{
    private readonly SystemEventWriter _writer;

    public SystemEventSinkFactory(SystemEventWriter writer) => _writer = writer;

    public ISearchEventSink For(string correlationId, string? userHash) =>
        new SystemEventSink(_writer, correlationId, userHash);
}
