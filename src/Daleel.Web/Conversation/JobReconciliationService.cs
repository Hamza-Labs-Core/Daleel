using Daleel.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Daleel.Web.Conversation;

/// <summary>
/// Periodic safety sweep for in-flight search jobs, complementing the boot-time
/// <see cref="OrphanedJobReconciler"/>. Every <see cref="SweepInterval"/> it reconciles two kinds of stuck
/// "running" jobs that the normal worker path can miss:
///
///   • <b>Force-cancel</b> — a job whose durable <c>CancelRequested</c> flag is set but is still "running"
///     (the workflow ignored the cooperative token and the worker hasn't re-checked yet). The flag is the
///     source of truth, so the sweep finalizes it as cancelled.
///   • <b>Hung</b> — a job "running" longer than <see cref="HungThreshold"/>. The runner self-cancels at a
///     10-minute deadline, so anything past 12 minutes is genuinely wedged (a non-cancellable native call,
///     a deadlock); it's failed so the user isn't left spinning forever.
///
/// Healthy in-flight jobs (running, not cancelled, under the threshold) are left untouched.
/// </summary>
public sealed class JobReconciliationService : BackgroundService
{
    /// <summary>How often the sweep runs.</summary>
    public static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// A job "running" longer than this is treated as hung. 12 minutes = the runner's 10-minute workflow
    /// deadline plus a 2-minute buffer, so a job that's merely slow (about to self-cancel) isn't failed early.
    /// </summary>
    public static readonly TimeSpan HungThreshold = TimeSpan.FromMinutes(12);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConversationBroadcaster _broadcaster;
    private readonly ILogger<JobReconciliationService> _logger;

    public JobReconciliationService(
        IServiceScopeFactory scopeFactory,
        IConversationBroadcaster broadcaster,
        ILogger<JobReconciliationService> logger)
    {
        _scopeFactory = scopeFactory;
        _broadcaster = broadcaster;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval);
        // Wait one interval before the first sweep: boot reconciliation already ran (OrphanedJobReconciler),
        // and any job started just before boot is healthy and under the hung threshold.
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await SweepAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw; // host shutdown
            }
            catch (Exception ex)
            {
                // A sweep failure (transient DB blip) must never take the timer down — try again next tick.
                _logger.LogError(ex, "Job reconciliation sweep failed");
            }
        }
    }

    /// <summary>Runs one reconciliation pass. Public so tests can drive it deterministically.</summary>
    public async Task SweepAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DaleelDbContext>();
        var convos = scope.ServiceProvider.GetRequiredService<IConversationStore>();

        var now = DateTimeOffset.UtcNow;
        var hungBefore = now - HungThreshold;

        var running = await db.SearchJobs
            .Where(j => j.Status == JobStatus.Running)
            .ToListAsync(ct);

        // Classify and mutate in memory; affected jobs are notified only after the row write commits, so
        // the durable terminal state is authoritative before any device is told the search ended.
        var affected = new List<SearchJob>();
        foreach (var job in running)
        {
            if (job.CancelRequested)
            {
                job.Status = JobStatus.Cancelled;
                job.CompletedAt = now;
                _logger.LogWarning(
                    "Sweep force-cancelling job {JobId} (user {UserId}): cancel was requested but the run was still active",
                    job.Id, job.UserId);
                affected.Add(job);
            }
            else if (job.StartedAt is { } started && started < hungBefore)
            {
                job.Status = JobStatus.Failed;
                job.Error = "Search exceeded the time limit and was stopped.";
                job.CompletedAt = now;
                _logger.LogWarning(
                    "Sweep failing hung job {JobId} (user {UserId}): running since {StartedAt:o}, past the {Minutes}-minute limit",
                    job.Id, job.UserId, started, HungThreshold.TotalMinutes);
                affected.Add(job);
            }
            // else: healthy in-flight job — leave it.
        }

        if (affected.Count == 0)
        {
            return;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        foreach (var job in affected)
        {
            // The durable terminal status (written above) is what stops the run: a still-executing worker
            // sees CancelRequested on its next per-activity check (or its pre-commit re-check) and bails.
            // Here we just mirror the terminal state onto the user's conversation + live UI so a watching
            // tab stops spinning.
            var isCancel = job.Status == JobStatus.Cancelled;
            await convos.CompleteAsync(job.UserId, isCancel ? "cancelled" : "error", null, null, now, ct)
                .ConfigureAwait(false);
            await _broadcaster.CompletedAsync(
                job.UserId, job.Id, job.Status, null, null,
                isCancel ? "Search cancelled." : "Search was interrupted. Please try again.")
                .ConfigureAwait(false);
        }

        _logger.LogWarning(
            "Job reconciliation sweep finalized {Count} stuck job(s): {Cancelled} cancelled, {Failed} hung",
            affected.Count,
            affected.Count(j => j.Status == JobStatus.Cancelled),
            affected.Count(j => j.Status == JobStatus.Failed));
    }
}
