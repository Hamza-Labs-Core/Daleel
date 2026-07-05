using Daleel.Web.Data;
using Daleel.Web.Events;
using Microsoft.EntityFrameworkCore;

namespace Daleel.Web.Pipeline.Enrichment;

/// <summary>Live counters for one work-item kind within the window.</summary>
public sealed record KindStats(
    string Kind, int Pending, int Running, int Done, int Dead,
    double AvgAttempts, double? AvgSecondsToDone);

/// <summary>A dead unit surfaced on the dashboard — the visible give-up ledger.</summary>
public sealed record DeadItem(
    long Id, string Kind, int SearchJobId, int Attempts, string? LastError, DateTimeOffset? CompletedAt);

/// <summary>Search-job counters: live states now + terminal outcomes within the window.</summary>
public sealed record JobStats(int Queued, int Running, int Completed, int Failed, int Cancelled);

/// <summary>Edge poll-drain counters from the system event log (null when the log is disabled).</summary>
public sealed record DrainStats(int Drained, int Failed);

/// <summary>Everything the /admin/queues live dashboard renders, computed in one pass.</summary>
public sealed record QueueDashboard(
    DateTimeOffset At,
    TimeSpan Window,
    int Pending,
    int Running,
    int Done,
    int Dead,
    int Recovered,
    TimeSpan? OldestPendingAge,
    IReadOnlyList<KindStats> Kinds,
    IReadOnlyList<DeadItem> RecentDead,
    JobStats Jobs,
    DrainStats? Drain);

/// <summary>
/// Read-side of the queues dashboard. Counts come straight from the queue/job tables (the queue's
/// only state, so the dashboard can never disagree with the consumer); drain counters come from the
/// system event log the poll drain already publishes into. Registered scoped — one DbContext per
/// refresh tick.
/// </summary>
public interface IQueueDashboardService
{
    Task<QueueDashboard> SnapshotAsync(TimeSpan window, CancellationToken ct = default);
}

public sealed class QueueDashboardService : IQueueDashboardService
{
    /// <summary>Dead rows listed on the dashboard (newest first) — the rest stay queryable in SQL.</summary>
    private const int RecentDeadCap = 20;

    private readonly DaleelDbContext _db;
    private readonly ISystemEventLog _systemLog;

    public QueueDashboardService(DaleelDbContext db, ISystemEventLog systemLog)
    {
        _db = db;
        _systemLog = systemLog;
    }

    public async Task<QueueDashboard> SnapshotAsync(TimeSpan window, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var since = now - window;

        // Live states (pending/running) are counted regardless of age — a week-old pending row is a
        // problem the dashboard must show, not hide; terminal states are windowed (throughput view).
        var rows = await _db.EnrichmentWorkItems.AsNoTracking()
            .Where(i =>
                i.Status == WorkItemStatus.Pending || i.Status == WorkItemStatus.Running ||
                i.CreatedAt >= since)
            .Select(i => new
            {
                i.Id, i.Kind, i.Status, i.Attempts, i.SearchJobId,
                i.LastError, i.CreatedAt, i.CompletedAt, i.NotBefore
            })
            .ToListAsync(ct);

        var kinds = rows
            .GroupBy(i => i.Kind)
            .Select(g => new KindStats(
                g.Key,
                Pending: g.Count(i => i.Status == WorkItemStatus.Pending),
                Running: g.Count(i => i.Status == WorkItemStatus.Running),
                Done: g.Count(i => i.Status == WorkItemStatus.Done),
                Dead: g.Count(i => i.Status == WorkItemStatus.Dead),
                AvgAttempts: g.Any(i => i.Status == WorkItemStatus.Done || i.Status == WorkItemStatus.Dead)
                    ? Math.Round(g.Where(i => i.Status is WorkItemStatus.Done or WorkItemStatus.Dead)
                        .Average(i => i.Attempts), 2)
                    : 0,
                AvgSecondsToDone: g.Any(i => i.Status == WorkItemStatus.Done && i.CompletedAt.HasValue)
                    ? Math.Round(g.Where(i => i.Status == WorkItemStatus.Done && i.CompletedAt.HasValue)
                        .Average(i => (i.CompletedAt!.Value - i.CreatedAt).TotalSeconds), 1)
                    : null))
            .OrderBy(k => k.Kind)
            .ToList();

        var pendingRows = rows.Where(i => i.Status == WorkItemStatus.Pending).ToList();
        var recentDead = rows
            .Where(i => i.Status == WorkItemStatus.Dead)
            .OrderByDescending(i => i.CompletedAt ?? DateTimeOffset.MinValue)
            .Take(RecentDeadCap)
            .Select(i => new DeadItem(i.Id, i.Kind, i.SearchJobId, i.Attempts, i.LastError, i.CompletedAt))
            .ToList();

        var jobs = await _db.SearchJobs.AsNoTracking()
            .Where(j => j.Status == JobStatus.Queued || j.Status == JobStatus.Running || j.CreatedAt >= since)
            .GroupBy(j => j.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        int JobCount(string status) => jobs.FirstOrDefault(j => j.Status == status)?.Count ?? 0;

        return new QueueDashboard(
            At: now,
            Window: window,
            Pending: pendingRows.Count,
            Running: rows.Count(i => i.Status == WorkItemStatus.Running),
            Done: rows.Count(i => i.Status == WorkItemStatus.Done),
            Dead: rows.Count(i => i.Status == WorkItemStatus.Dead),
            // Units that failed at least once and still made it — the retry machinery earning its keep.
            Recovered: rows.Count(i => i.Status == WorkItemStatus.Done && i.Attempts > 1),
            OldestPendingAge: pendingRows.Count == 0
                ? null
                : now - pendingRows.Min(i => i.CreatedAt),
            Kinds: kinds,
            RecentDead: recentDead,
            Jobs: new JobStats(
                Queued: JobCount(JobStatus.Queued),
                Running: JobCount(JobStatus.Running),
                Completed: JobCount(JobStatus.Completed),
                Failed: JobCount(JobStatus.Failed),
                Cancelled: JobCount(JobStatus.Cancelled)),
            Drain: await DrainSnapshotAsync(since, ct));
    }

    /// <summary>
    /// Drain counters from the events the poll drain already publishes (store.prices.drained /
    /// store.prices.edge_failed). Windowed page counts — null when the event log is disabled, so the
    /// page can show "not configured" instead of zeros that look like a dead drain.
    /// </summary>
    private async Task<DrainStats?> DrainSnapshotAsync(DateTimeOffset since, CancellationToken ct)
    {
        if (!_systemLog.IsEnabled)
        {
            return null;
        }

        try
        {
            var drained = await _systemLog.QueryAsync(new SystemEventQuery
            {
                Since = since, Text = "store.prices.drained", PageSize = 1
            }, ct);
            var failed = await _systemLog.QueryAsync(new SystemEventQuery
            {
                Since = since, Text = "store.prices.edge_failed", PageSize = 1
            }, ct);
            return new DrainStats(drained.Total, failed.Total);
        }
        catch
        {
            return null; // the dashboard must render even when the event backend hiccups
        }
    }
}
