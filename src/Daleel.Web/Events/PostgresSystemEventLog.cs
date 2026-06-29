using Microsoft.EntityFrameworkCore;

namespace Daleel.Web.Events;

/// <summary>
/// The live unified activity log, backed by <see cref="EventStoreDbContext"/> on PostgreSQL (the same
/// <c>daleel_events</c> database as the pipeline firehose). Writes are best-effort and never throw into
/// the caller — a telemetry hiccup must not fail a user's search or login. Reads push the indexed
/// predicates (time window, correlation id, user hash) to the database, cap the window, then hand the
/// rows to the pure <see cref="SystemEventFilters"/> for category / severity / free-text matching and
/// pagination — mirroring how <see cref="PostgresEventStore"/> keeps its aggregation a tested pure function.
/// </summary>
public sealed class PostgresSystemEventLog : ISystemEventLog
{
    /// <summary>
    /// Safety cap on how many rows a single timeline query pulls into memory for the pure filters. The
    /// admin timeline is moderate-volume and already time-windowed; this bounds a pathological "all time,
    /// no filter" request. When a window hits the cap the page still renders (newest-first), it just can't
    /// see past the cap — acceptable for an operator feed, and logged so it's diagnosable.
    /// </summary>
    public const int WindowCap = 10_000;

    private readonly IDbContextFactory<EventStoreDbContext> _factory;
    private readonly ILogger<PostgresSystemEventLog> _logger;

    public PostgresSystemEventLog(
        IDbContextFactory<EventStoreDbContext> factory, ILogger<PostgresSystemEventLog> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public bool IsEnabled => true;

    public Task PublishAsync(SystemEvent ev, CancellationToken ct = default) =>
        PublishManyAsync(new[] { ev }, ct);

    public async Task PublishManyAsync(IReadOnlyCollection<SystemEvent> events, CancellationToken ct = default)
    {
        if (events.Count == 0)
        {
            return;
        }

        try
        {
            await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
            db.SystemEvents.AddRange(events);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Telemetry is non-critical: swallow so a Postgres hiccup never breaks the action being logged.
            _logger.LogWarning(ex, "Failed to persist {Count} system event(s)", events.Count);
        }
    }

    public async Task<SystemEventPage> QueryAsync(SystemEventQuery query, CancellationToken ct = default)
    {
        try
        {
            await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);

            // Indexed predicates → the database. Newest first, bounded by the window cap.
            IQueryable<SystemEvent> q = db.SystemEvents.AsNoTracking();
            if (query.Since is { } since)
            {
                q = q.Where(e => e.Timestamp >= since);
            }
            if (query.Until is { } until)
            {
                q = q.Where(e => e.Timestamp < until);
            }
            if (!string.IsNullOrWhiteSpace(query.CorrelationId))
            {
                q = q.Where(e => e.CorrelationId == query.CorrelationId);
            }
            if (!string.IsNullOrWhiteSpace(query.UserHash))
            {
                q = q.Where(e => e.UserHash == query.UserHash);
            }

            var window = await q
                .OrderByDescending(e => e.Timestamp)
                .ThenByDescending(e => e.Id)
                .Take(WindowCap)
                .ToListAsync(ct).ConfigureAwait(false);

            if (window.Count >= WindowCap)
            {
                _logger.LogInformation(
                    "System-event timeline query hit the {Cap}-row window cap; older rows are not visible for this filter",
                    WindowCap);
            }

            // Category / severity / free-text + pagination → pure, tested logic.
            var filtered = SystemEventFilters.Apply(window, query).ToList();
            var items = SystemEventFilters.Page(filtered, query);
            return new SystemEventPage(items, filtered.Count, Math.Max(1, query.Page), query.PageSize);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to query system-event timeline");
            return SystemEventPage.Empty(query);
        }
    }
}
