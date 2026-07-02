using Daleel.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Daleel.Web.Conversation;

/// <summary>
/// Boot-time crash recovery for in-flight search work. A job's in-flight workflow runs only in process, so
/// when the container dies mid-search (deploy, crash, OOM) every job left as <see cref="JobStatus.Running"/>
/// in Postgres is orphaned: nothing is executing it any more, yet the row says "running" forever and the
/// user's UI spins indefinitely. (A "queued" job needs no recovery — the worker's poll simply claims it.)
///
/// This reconciles those zombies once at startup — BEFORE <see cref="SearchJobService"/> begins consuming new
/// work — by failing each orphaned job and flipping the matching conversation to a terminal "interrupted"
/// state so the UI can stop spinning and prompt a retry. It is invoked from <c>Program.EnsureDatabase</c>,
/// which runs synchronously before <c>app.Run()</c> starts the hosted worker, guaranteeing the ordering.
/// </summary>
public static class OrphanedJobReconciler
{
    /// <summary>Reason stamped on jobs that were left "running" by a previous container's death.</summary>
    public const string OrphanReason = "Orphaned by container restart";

    /// <summary>Terminal conversation status the UI maps to "Search was interrupted, please try again".</summary>
    public const string InterruptedStatus = "interrupted";

    /// <summary>
    /// Fails every job still marked <see cref="JobStatus.Running"/> and resets every conversation still
    /// pointing at one. Idempotent: a clean boot (no running rows) is a no-op.
    /// </summary>
    public static async Task ReconcileAsync(DaleelDbContext db, ILogger logger, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;

        var orphaned = await db.SearchJobs
            .Where(j => j.Status == JobStatus.Running)
            .ToListAsync(ct);

        foreach (var job in orphaned)
        {
            job.Status = JobStatus.Failed;
            job.Error = OrphanReason;
            job.CompletedAt = now;
            // One warning per zombie so operators can see exactly which searches were lost to the restart.
            logger.LogWarning(
                "Auto-failing orphaned search job {JobId} (user {UserId}, query \"{Query}\") left \"running\" by a container restart",
                job.Id, job.UserId, job.Query);
        }

        // Every running job was just failed, so every conversation still showing "running" is stale — flip it
        // to the interrupted state. Done as a set rather than per-job: a conversation tracks the user's single
        // active job, so "running conversation" and "orphaned job" are the same population.
        var stuckConvos = await db.UserConversations
            .Where(c => c.CurrentStatus == "running")
            .ToListAsync(ct);

        foreach (var convo in stuckConvos)
        {
            convo.CurrentStatus = InterruptedStatus;
            convo.CompletedAt = now;
        }

        if (orphaned.Count == 0 && stuckConvos.Count == 0)
        {
            return;
        }

        await db.SaveChangesAsync(ct);
        logger.LogWarning(
            "Startup reconciliation auto-failed {JobCount} orphaned running job(s) and reset {ConvoCount} stuck conversation(s)",
            orphaned.Count, stuckConvos.Count);
    }
}
