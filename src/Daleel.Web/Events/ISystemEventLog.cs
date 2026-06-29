using System.Text.Json;

namespace Daleel.Web.Events;

/// <summary>
/// The append-only unified activity log behind the admin event timeline. Writes are best-effort and must
/// never throw into the caller (recording an event must not fail a search, a login, or a sweep); reads
/// power the timeline page. Backed by <see cref="PostgresSystemEventLog"/>, or the no-op
/// <see cref="NullSystemEventLog"/> when no Postgres is configured.
/// </summary>
public interface ISystemEventLog
{
    /// <summary>Whether the log is active; the timeline shows a "not configured" hint when false.</summary>
    bool IsEnabled { get; }

    /// <summary>Records one event (best-effort — swallows write failures).</summary>
    Task PublishAsync(SystemEvent ev, CancellationToken ct = default);

    /// <summary>Records a batch of events in one round-trip (best-effort).</summary>
    Task PublishManyAsync(IReadOnlyCollection<SystemEvent> events, CancellationToken ct = default);

    /// <summary>Runs a filtered, paged timeline query (newest first) and returns the page + total count.</summary>
    Task<SystemEventPage> QueryAsync(SystemEventQuery query, CancellationToken ct = default);
}

/// <summary>
/// Convenience builders so emission sites stay one-liners — <c>log.LogAsync(category, type, summary, …)</c>
/// — instead of constructing a <see cref="SystemEvent"/> by hand. Details are serialized to jsonb here.
/// </summary>
public static class SystemEventLogExtensions
{
    /// <summary>Builds and records a single event from its parts. Details (when given) become the jsonb payload.</summary>
    public static Task LogAsync(
        this ISystemEventLog log,
        string category,
        string eventType,
        string summary,
        string severity = SystemEventSeverity.Info,
        string source = "",
        string? correlationId = null,
        string? userHash = null,
        IReadOnlyDictionary<string, object?>? details = null,
        CancellationToken ct = default)
    {
        if (!log.IsEnabled)
        {
            return Task.CompletedTask;
        }

        var ev = new SystemEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            Category = category,
            EventType = eventType,
            Severity = severity,
            Source = source,
            Summary = summary,
            CorrelationId = correlationId,
            UserHash = userHash,
            DetailsJson = details is null ? "{}" : JsonSerializer.Serialize(details)
        };
        return log.PublishAsync(ev, ct);
    }
}

/// <summary>The log used when no Postgres connection is configured: silently drops every event.</summary>
public sealed class NullSystemEventLog : ISystemEventLog
{
    public bool IsEnabled => false;

    public Task PublishAsync(SystemEvent ev, CancellationToken ct = default) => Task.CompletedTask;

    public Task PublishManyAsync(IReadOnlyCollection<SystemEvent> events, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<SystemEventPage> QueryAsync(SystemEventQuery query, CancellationToken ct = default) =>
        Task.FromResult(SystemEventPage.Empty(query));
}
