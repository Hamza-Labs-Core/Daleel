using System.Collections.Concurrent;
using System.Diagnostics;
using Daleel.Web.Data;
using Daleel.Web.Events;
using Daleel.Web.Pipeline;
using Microsoft.EntityFrameworkCore;

namespace Daleel.Web.Conversation;

/// <summary>
/// The async backend worker. Polls Postgres for queued <see cref="SearchJob"/>s, runs the agent off the
/// HTTP thread, streams progress to the user's devices over SignalR, and persists the result + the user's
/// active conversation. Supports cooperative cancellation via the durable <c>CancelRequested</c> flag.
///
/// There is NO in-memory queue or running-job registry: the only state is the <c>SearchJobs</c> table.
/// A restarted container just resumes polling and picks up whatever is still <c>queued</c> — nothing is
/// lost to a process death.
/// </summary>
public sealed class SearchJobService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConversationBroadcaster _broadcaster;
    private readonly ILogger<SearchJobService> _logger;
    private readonly Pipeline.Enrichment.IEnrichmentWorkQueue _enrichQueue;

    /// <summary>
    /// How long to idle between polls when no job is waiting. Short enough that a freshly-queued search
    /// starts promptly, long enough that an idle worker isn't hammering Postgres. A claim happens
    /// immediately whenever the previous one found work, so this only bounds the empty-queue latency.
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    public SearchJobService(
        IServiceScopeFactory scopeFactory,
        IConversationBroadcaster broadcaster,
        ILogger<SearchJobService> logger,
        Pipeline.Enrichment.IEnrichmentWorkQueue enrichQueue)
    {
        _scopeFactory = scopeFactory;
        _broadcaster = broadcaster;
        _logger = logger;
        _enrichQueue = enrichQueue;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // CONCURRENT JOBS: the loop keeps claiming while work is queued and runs each job as its own
        // tracked task — one user's slow search never queues behind another's. No job-count cap by
        // default (each job is individually bounded by its own deadline + cost cap); the semaphore is
        // an optional per-environment RESTRAINT via PIPELINE_MAX_CONCURRENT_JOBS. FOR UPDATE SKIP
        // LOCKED already makes claims safe across concurrent claimers, in-process or cross-container.
        using var jobGate = new SemaphoreSlim(Pipeline.PipelineLimits.ConcurrentJobs);
        var running = new ConcurrentDictionary<Task, byte>();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await jobGate.WaitAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // host shutdown
            }

            // The claim try-block must contain NOTHING that runs after the permit was returned —
            // otherwise a shutdown-cancelled Delay lands in a catch that Releases a second time
            // (SemaphoreFullException) and the drain below never runs. Idle/backoff waits happen
            // OUTSIDE the try, each with its own cancellation-safe break.
            int jobId;
            var backOff = false;
            try
            {
                if (await ClaimNextQueuedJobAsync(stoppingToken) is not { } claimed)
                {
                    // Nothing waiting — idle a beat, then poll again. Source of truth is the DB, so a job
                    // queued by another process (or a restart's leftover work) is picked up next tick.
                    jobGate.Release();
                    backOff = true;
                    jobId = 0;
                }
                else
                {
                    jobId = claimed;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                jobGate.Release();
                break; // host shutdown
            }
            catch (Exception ex)
            {
                // A claim failure (transient DB blip) must never take the worker down — back off and retry.
                jobGate.Release();
                _logger.LogError(ex, "Failed to claim the next queued search job");
                backOff = true;
                jobId = 0;
            }

            if (backOff)
            {
                try
                {
                    await Task.Delay(PollInterval, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break; // host shutdown — fall through to the in-flight-job drain
                }
                continue;
            }

            // Run the job detached and immediately go claim the next one — queued searches start NOW,
            // not after the previous search finishes. One job's failure never touches the others.
            var task = Task.Run(async () =>
            {
                try
                {
                    await ProcessAsync(jobId, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // host shutdown — the job's own interrupted-state reconciler handles the leftovers
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Search job {JobId} crashed the processor", jobId);
                }
                finally
                {
                    // Shutdown race: a job outliving the 10s drain finds ExecuteAsync gone and the
                    // gate disposed — nothing waits on it anymore, so the ODE is meaningless noise.
                    try { jobGate.Release(); }
                    catch (ObjectDisposedException) { }
                }
            }, CancellationToken.None);
            running[task] = 0;
            _ = task.ContinueWith(t => running.TryRemove(t, out _), TaskScheduler.Default);
        }

        // Drain: give in-flight jobs a moment to observe the stop token and finalize their state.
        try
        {
            await Task.WhenAll(running.Keys).WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        }
        catch
        {
            // Shutdown drain is best-effort — the orphaned-job reconciler recovers anything cut off.
        }
    }

    /// <summary>
    /// Atomically claims the oldest <c>queued</c> job and flips it to <c>running</c>, returning its id (or
    /// null when nothing is waiting). The claim is the entire job-dispatch mechanism — there is no in-memory
    /// queue.
    ///
    /// <c>FOR UPDATE SKIP LOCKED</c> is the canonical Postgres work-queue claim: the row this transaction
    /// locks is skipped by any other polling worker (or container), so the same job can never be handed to
    /// two workers. Committing the <c>running</c> write inside the same transaction is what removes the row
    /// from every subsequent poll. A job cancelled while still queued has <c>Status = cancelled</c> already,
    /// so this only ever picks up genuinely pending work.
    /// </summary>
    private async Task<int?> ClaimNextQueuedJobAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DaleelDbContext>();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var job = await db.SearchJobs
            .FromSqlRaw(
                """
                SELECT * FROM "SearchJobs"
                WHERE "Status" = {0}
                ORDER BY "CreatedAt"
                LIMIT 1
                FOR UPDATE SKIP LOCKED
                """,
                JobStatus.Queued)
            .FirstOrDefaultAsync(ct);

        if (job is null)
        {
            await tx.RollbackAsync(ct);
            return null;
        }

        job.Status = JobStatus.Running;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return job.Id;
    }

    /// <summary>Processes a single job. Public so tests can drive it deterministically.</summary>
    public async Task ProcessAsync(int jobId, CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DaleelDbContext>();
        var runner = scope.ServiceProvider.GetRequiredService<ISearchRunner>();
        var convos = scope.ServiceProvider.GetRequiredService<IConversationStore>();
        var analytics = scope.ServiceProvider.GetRequiredService<IAnalyticsService>();
        var history = scope.ServiceProvider.GetRequiredService<ISearchHistoryRepository>();
        var quota = scope.ServiceProvider.GetRequiredService<IQuotaService>();
        var systemLog = scope.ServiceProvider.GetRequiredService<ISystemEventLog>();

        var job = await db.SearchJobs.FirstOrDefaultAsync(j => j.Id == jobId, stoppingToken);
        if (job is null || job.Status is JobStatus.Cancelled or JobStatus.Completed)
        {
            return;
        }

        // The unified timeline's correlation id is the job id; the actor is the hashed (never raw) user.
        var correlationId = job.Id.ToString();
        var userHash = Anonymizer.HashUserId(job.UserId);

        var now = DateTimeOffset.UtcNow;
        job.Status = JobStatus.Running;
        job.StartedAt = now;
        job.ProgressMessage = "🔍 Generating search strategy…"; // plain English for the server-side job row
        await db.SaveChangesAsync(stoppingToken);
        await convos.SetRunningAsync(job.UserId, job.Id, job.Query, now, stoppingToken);
        await systemLog.LogAsync(
            SystemEventCategory.Search, "search.started", $"Search started: {job.Query}",
            source: "search-worker", correlationId: correlationId, userHash: userHash,
            details: new Dictionary<string, object?>
            {
                ["query"] = job.Query, ["queryType"] = job.QueryType, ["geo"] = job.Geo, ["model"] = job.Model
            }, ct: stoppingToken);
        // Broadcast a structured signal so every device localizes the first line and the stepper lights
        // up step 1 immediately (before the first activity reports in).
        await _broadcaster.ProgressAsync(
            job.UserId, job.Id, SearchProgressSignal.Encode(SearchStep.Analyzing, "Progress.Msg.Strategy"));

        var sw = Stopwatch.StartNew();
        try
        {
            // Stream curated status to all devices (fire-and-forget; don't touch DbContext here).
            // Internal provider diagnostics ("… failed: …") are agent-level noise — keep them in the
            // server log, never surface raw error text in the user's status line.
            void Progress(string message)
            {
                if (message.Contains("failed", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("Search job {JobId} · {Message}", job.Id, message);
                    return;
                }
                _ = _broadcaster.ProgressAsync(job.UserId, job.Id, message);
            }

            var result = await runner.RunAsync(job, Progress, stoppingToken);
            sw.Stop();

            // Force-cancel backstop: the durable CancelRequested flag is the source of truth. If the user
            // cancelled while the run was in flight and the workflow ignored the cooperative token (or the
            // periodic sweep already flipped the row), discard the freshly-built result and finalize as
            // cancelled — don't complete, don't charge credits, don't enrich. ReloadAsync refreshes the flag
            // and status, which were written on a different DbContext (the UI's, or the sweep's).
            await db.Entry(job).ReloadAsync(stoppingToken);
            if (job.CancelRequested || job.Status == JobStatus.Cancelled)
            {
                await FinishAsync(db, convos, job, JobStatus.Cancelled, "cancelled", error: null, stoppingToken);
                await _broadcaster.CompletedAsync(job.UserId, job.Id, "cancelled", null, null, "Search cancelled.");
                await systemLog.LogAsync(
                    SystemEventCategory.Search, "search.cancelled", $"Search cancelled: {job.Query}",
                    severity: SystemEventSeverity.Warning, source: "search-worker",
                    correlationId: correlationId, userHash: userHash,
                    details: new Dictionary<string, object?> { ["query"] = job.Query, ["forced"] = true },
                    ct: CancellationToken.None);
                return;
            }

            job.Status = JobStatus.Completed;
            job.ResultJson = result.ResultJson;
            job.ProgressMessage = "✅ Report ready!";
            job.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(stoppingToken);

            // Charge the search's actual credit cost now that the provider calls are known. Best-effort:
            // a billing hiccup must never fail a search the user already received.
            try
            {
                await quota.ChargeCreditsAsync(job.UserId, result.Credits, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to charge {Credits} credits for job {JobId}", result.Credits, job.Id);
            }

            await convos.CompleteAsync(job.UserId, job.Id, "completed", result.ResultJson, result.ResultType, job.CompletedAt.Value, stoppingToken);
            // Keep the generated Id: the background enrichers update THIS exact row when they finish,
            // so a late-landing enrichment can never overwrite a newer same-query history entry.
            var historyEntry = await history.AddAsync(new SearchHistoryEntry
            {
                UserId = job.UserId, Query = job.Query, QueryType = job.QueryType,
                Geo = job.Geo, Model = job.Model, ResultSummary = null,
                // Persist the full result so opening this entry later re-displays it without re-running.
                ResultJson = result.ResultJson,
                // Base-run credits; background enrichment increments this via AddCreditsAsync as it runs.
                Credits = result.Credits,
                CreatedAt = job.CompletedAt.Value
            }, stoppingToken);
            // Record the search for analytics / cost optimisation — providers called, api-call
            // count, result count and timing. This is server-side telemetry, never shown to the user.
            await analytics.RecordSearchAsync(new AnalyticsEvent
            {
                // Anonymous analytics: a one-way hash of the user id, never the id itself.
                UserId = Anonymizer.HashUserId(job.UserId), Query = job.Query, QueryType = job.QueryType, Geo = job.Geo,
                Model = job.Model, DurationMs = (int)sw.ElapsedMilliseconds,
                ResultCount = result.ResultCount, ApiCallsMade = result.ApiCalls,
                Provider = string.IsNullOrWhiteSpace(result.Providers) ? null : result.Providers,
                FilteredCount = result.FilteredCount, FilteredCategories = result.FilteredCategories
            }, stoppingToken);

            await _broadcaster.CompletedAsync(job.UserId, job.Id, "completed", result.ResultJson, result.ResultType, null);
            await systemLog.LogAsync(
                SystemEventCategory.Search, "search.completed",
                $"Search completed: {job.Query} ({result.ResultCount} result(s))",
                source: "search-worker", correlationId: correlationId, userHash: userHash,
                details: new Dictionary<string, object?>
                {
                    ["query"] = job.Query, ["resultCount"] = result.ResultCount,
                    ["apiCalls"] = result.ApiCalls, ["providers"] = result.Providers,
                    ["filteredCount"] = result.FilteredCount, ["durationMs"] = sw.ElapsedMilliseconds,
                    ["servedFromCache"] = result.CacheQuality is not null
                }, ct: stoppingToken);

            // Post-result follow-up goes through the durable enrichment WORK QUEUE: one row here, and
            // the consumer fans it out into per-item/per-store/per-brand units, each with its own
            // retry budget, each persisting the moment it finishes. Nothing runs under a shared
            // lifetime, so nothing can be abandoned wholesale — a deploy mid-enrichment just means
            // the remaining units run after the restart.
            if (result.CacheQuality is { } quality)
            {
                // Served from cache. A below-threshold hit was shown immediately; the gap refill unit
                // re-scrapes ONLY the pieces the quality validator flagged as missing. A complete
                // (ServeAsIs) hit needs nothing further.
                if (quality.Decision == CacheDecision.ServeAndEnrich)
                {
                    await EnqueueEnrichmentAsync(
                        job, historyEntry.Id, result, Pipeline.Enrichment.EnrichmentUnit.CacheGapRefill, quality);
                }
            }
            else if (result.CostCapTripped)
            {
                // The per-job cost cap cut the base run short (the result above was salvaged). The
                // queue consumer enforces the same cumulative cap per unit, but not enqueueing at all
                // is cheaper and clearer: the user keeps the salvaged base result.
                _logger.LogWarning(
                    "Skipping background enrichment for job {JobId}: the per-job cost cap tripped during the base run",
                    job.Id);
            }
            else
            {
                // Fresh run: the plan unit fans out progressive enrichment — the UI fills images,
                // prices and specs in place as each unit lands.
                await EnqueueEnrichmentAsync(
                    job, historyEntry.Id, result, Pipeline.Enrichment.EnrichmentUnit.Plan, quality: null);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw; // host shutdown — let the worker loop exit, don't mislabel the job as cancelled/failed
        }
        catch (Exception ex)
        {
            // The run threw. Distinguish a user cancel from a genuine failure using the durable
            // CancelRequested flag — the source of truth — not the exception type: CancellableActivity
            // throws an OperationCanceledException the moment it sees the flag, but a deadline or provider
            // error throws too. Reload to read the flag (written on the UI's / sweep's DbContext); the
            // reload is best-effort so a DB blip here can't itself crash the handler.
            try
            {
                await db.Entry(job).ReloadAsync(CancellationToken.None);
            }
            catch (Exception reloadEx)
            {
                _logger.LogWarning(reloadEx, "Could not reload job {JobId} to classify its failure", job.Id);
            }

            if (job.CancelRequested || job.Status == JobStatus.Cancelled)
            {
                await FinishAsync(db, convos, job, JobStatus.Cancelled, "cancelled", error: null, CancellationToken.None);
                await _broadcaster.CompletedAsync(job.UserId, job.Id, "cancelled", null, null, "Search cancelled.");
                await systemLog.LogAsync(
                    SystemEventCategory.Search, "search.cancelled", $"Search cancelled: {job.Query}",
                    severity: SystemEventSeverity.Warning, source: "search-worker",
                    correlationId: correlationId, userHash: userHash,
                    details: new Dictionary<string, object?> { ["query"] = job.Query }, ct: CancellationToken.None);
                return;
            }

            // Genuine failure. Log the full exception server-side; show the user a generic message so
            // provider/LLM/DB detail (hostnames, partial keys in URLs, SQL text) never reaches the browser.
            // The raw message is kept in the job row (server-only) for operator diagnostics.
            _logger.LogError(ex, "Search job {JobId} failed", job.Id);
            await FinishAsync(db, convos, job, JobStatus.Failed, "error", ex.Message, CancellationToken.None);
            await _broadcaster.CompletedAsync(job.UserId, job.Id, "failed", null, null,
                "Search failed. Please try again.");
            // The full message is operator-only telemetry (the user saw a generic line), so it's safe in
            // the admin timeline's detail payload.
            await systemLog.LogAsync(
                SystemEventCategory.Search, "search.failed", $"Search failed: {job.Query}",
                severity: SystemEventSeverity.Error, source: "search-worker",
                correlationId: correlationId, userHash: userHash,
                details: new Dictionary<string, object?>
                {
                    ["query"] = job.Query, ["error"] = ex.Message, ["exceptionType"] = ex.GetType().Name
                }, ct: CancellationToken.None);
        }
    }

    /// <summary>
    /// Queues the post-result enrichment root unit. One insert — the queue consumer does the rest
    /// (fan-out, retries, incremental patches, live repaints). Failure to enqueue is logged loudly:
    /// the base result is already delivered, but the deep-dive would silently never happen.
    /// </summary>
    private async Task EnqueueEnrichmentAsync(
        SearchJob job, int historyId, SearchRunResult result, string kind, CacheQualityReport? quality)
    {
        try
        {
            var payload = Pipeline.Enrichment.EnrichmentWorkQueue.Payload(new Pipeline.Enrichment.PlanPayload(
                quality is null ? null : System.Text.Json.JsonSerializer.Serialize(quality),
                result.FilteredCount, result.FilteredCategories));
            await _enrichQueue.EnqueueAsync(new[]
            {
                new EnrichmentWorkItem
                {
                    SearchJobId = job.Id,
                    UserId = job.UserId,
                    HistoryEntryId = historyId,
                    ResultType = result.ResultType,
                    Kind = kind,
                    Payload = payload,
                    // The gap refill wraps a whole multi-phase pass, so a second full attempt is the
                    // most a transient failure deserves; fan-out units carry the default budget.
                    MaxAttempts = kind == Pipeline.Enrichment.EnrichmentUnit.CacheGapRefill ? 2 : 4
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enqueue enrichment for job {JobId} — deep-dive will not run", job.Id);
        }
    }


    private static async Task FinishAsync(
        DaleelDbContext db, IConversationStore convos, SearchJob job,
        string jobStatus, string convoStatus, string? error, CancellationToken ct)
    {
        job.Status = jobStatus;
        job.Error = error;
        job.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await convos.CompleteAsync(job.UserId, job.Id, convoStatus, null, null, job.CompletedAt.Value, ct);
    }
}
