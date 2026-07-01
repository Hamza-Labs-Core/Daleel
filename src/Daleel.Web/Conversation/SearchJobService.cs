using System.Diagnostics;
using Daleel.Web.Data;
using Daleel.Web.Events;
using Daleel.Web.Pipeline;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

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
    private readonly TimeSpan _enrichTimeout;

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
        IConfiguration config)
    {
        _scopeFactory = scopeFactory;
        _broadcaster = broadcaster;
        _logger = logger;
        _enrichTimeout = ResolveEnrichTimeout(config);
    }

    /// <summary>
    /// Hard ceiling on the detached enrichment pass so a hung scrape can't leak a worker. Configurable via
    /// <c>Enrichment:TimeoutSeconds</c> (default <see cref="DefaultEnrichTimeoutSeconds"/>); generous because
    /// the deep-dive also harvests store catalogues for live prices (a slow site crawl). Clamped to a sane
    /// floor so a misconfiguration can't make it effectively zero.
    /// </summary>
    private const int DefaultEnrichTimeoutSeconds = 180;

    private static TimeSpan ResolveEnrichTimeout(IConfiguration config)
    {
        var seconds = config.GetValue<int?>("Enrichment:TimeoutSeconds") ?? DefaultEnrichTimeoutSeconds;
        return TimeSpan.FromSeconds(Math.Max(30, seconds));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            int jobId;
            try
            {
                if (await ClaimNextQueuedJobAsync(stoppingToken) is not { } claimed)
                {
                    // Nothing waiting — idle a beat, then poll again. Source of truth is the DB, so a job
                    // queued by another process (or a restart's leftover work) is picked up next tick.
                    await Task.Delay(PollInterval, stoppingToken);
                    continue;
                }
                jobId = claimed;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw; // host shutdown
            }
            catch (Exception ex)
            {
                // A claim failure (transient DB blip) must never take the worker down — back off and retry.
                _logger.LogError(ex, "Failed to claim the next queued search job");
                await Task.Delay(PollInterval, stoppingToken);
                continue;
            }

            // One job's failure must never take down the worker loop.
            try
            {
                await ProcessAsync(jobId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw; // host shutdown — let the worker loop exit, don't mislabel it as a crash
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Search job {JobId} crashed the processor", jobId);
            }
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

            await convos.CompleteAsync(job.UserId, "completed", result.ResultJson, result.ResultType, job.CompletedAt.Value, stoppingToken);
            await history.AddAsync(new SearchHistoryEntry
            {
                UserId = job.UserId, Query = job.Query, QueryType = job.QueryType,
                Geo = job.Geo, Model = job.Model, ResultSummary = null,
                // Persist the full result so opening this entry later re-displays it without re-running.
                ResultJson = result.ResultJson,
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

            // Post-result follow-up depends on where this result came from. DETACHED on purpose: it must
            // never block this worker from picking up the next queued search, so a slow/hanging scrape
            // can't make subsequent searches "stuck". Each runs in its own DI scope with a hard timeout;
            // failures are swallowed (the base result is already delivered).
            if (result.CacheQuality is { } quality)
            {
                // Served from cache. A below-threshold hit was shown immediately; now re-scrape ONLY the
                // pieces the quality validator flagged as missing and stream the refilled result. A
                // complete (ServeAsIs) hit needs nothing further.
                if (quality.Decision == CacheDecision.ServeAndEnrich)
                {
                    _ = ReEnrichInBackgroundAsync(job.Id, job.UserId, result, quality);
                }
            }
            else if (result.CostCapTripped)
            {
                // The per-job cost cap cut the base run short (the result above was salvaged). Launching
                // the background deep-dive now would hand the very job the cap just stopped a FRESH full
                // enrichment budget — doubling the admin-configured ceiling exactly when it fired. The
                // user keeps the salvaged base result; enrichment is deliberately skipped.
                _logger.LogWarning(
                    "Skipping background enrichment for job {JobId}: the per-job cost cap tripped during the base run",
                    job.Id);
            }
            else
            {
                // Fresh run: progressive enrichment — base results are already on screen, now deep-dive
                // each item (official-brand-site specs via Context.dev) and stream the refreshed result
                // so the UI fills specs in place.
                _ = EnrichInBackgroundAsync(job.Id, job.UserId, result);
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
    /// Runs the post-result item deep-dive OFF the worker loop, in its own DI scope and under a hard
    /// timeout, then streams the enriched result. Fire-and-forget: never awaited by the job processor,
    /// so it can't block the queue; all failures are logged (never silently swallowed).
    /// </summary>
    private async Task EnrichInBackgroundAsync(int jobId, string userId, SearchRunResult baseResult)
    {
        using var timeout = new CancellationTokenSource(_enrichTimeout);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var sp = scope.ServiceProvider;
            var runner = sp.GetRequiredService<ISearchRunner>();
            var db = sp.GetRequiredService<DaleelDbContext>();
            var convos = sp.GetRequiredService<IConversationStore>();

            var job = await db.SearchJobs.FirstOrDefaultAsync(j => j.Id == jobId, timeout.Token);
            if (job is null)
            {
                return;
            }

            // Enrichment runs AFTER the result is on screen, so its progress must NOT be broadcast as
            // normal progress — that would flip the UI back to "running" and hide the completed result.
            // The UI shows a quiet "fetching specs…" hint instead; here we only log server-side.
            void Progress(string message) => _logger.LogDebug("Enrich job {JobId}: {Message}", jobId, message);

            var enriched = await runner.EnrichAsync(job, baseResult, Progress, timeout.Token);
            if (enriched is null)
            {
                return; // nothing changed — no UI update
            }

            job.ResultJson = enriched.ResultJson;
            await db.SaveChangesAsync(timeout.Token);
            await convos.CompleteAsync(
                userId, "completed", enriched.ResultJson, enriched.ResultType, DateTimeOffset.UtcNow, timeout.Token);
            await _broadcaster.EnrichedAsync(userId, jobId, enriched.ResultJson, enriched.ResultType);
        }
        catch (OperationCanceledException)
        {
            // Timed out — the base result is already on screen so there's nothing to undo, but abandoning
            // the deep-dive silently hid that the enriched specs/prices were never persisted. Surface it so
            // a chronically-too-short timeout (or a hung scrape) is diagnosable and the limit can be tuned.
            _logger.LogWarning(
                "Background enrichment for job {JobId} timed out after {TimeoutSeconds:n0}s and was abandoned; " +
                "the base result stands but the deep-dive did not complete. Raise Enrichment:TimeoutSeconds if this recurs.",
                jobId, _enrichTimeout.TotalSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Background enrichment failed for job {JobId}", jobId);
        }
    }

    /// <summary>
    /// Runs the smart-cache partial re-enrichment OFF the worker loop, in its own DI scope and under the
    /// same hard timeout as the item deep-dive, then streams the refilled result. Fire-and-forget: never
    /// awaited by the job processor, so it can't block the queue; all failures are logged and swallowed.
    /// The base (cached) result is already on screen — this only refills the gaps the quality validator
    /// found and re-renders in place via <c>Enriched</c>.
    /// </summary>
    private async Task ReEnrichInBackgroundAsync(
        int jobId, string userId, SearchRunResult baseResult, CacheQualityReport quality)
    {
        using var timeout = new CancellationTokenSource(_enrichTimeout);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var sp = scope.ServiceProvider;
            var runner = sp.GetRequiredService<ISearchRunner>();
            var db = sp.GetRequiredService<DaleelDbContext>();
            var convos = sp.GetRequiredService<IConversationStore>();

            var job = await db.SearchJobs.FirstOrDefaultAsync(j => j.Id == jobId, timeout.Token);
            if (job is null)
            {
                return;
            }

            // Like the item deep-dive, this runs AFTER the result is on screen, so its progress must NOT
            // be broadcast as normal progress (that would flip the UI back to "running"); only log it.
            void Progress(string message) => _logger.LogDebug("Re-enrich job {JobId}: {Message}", jobId, message);

            _logger.LogInformation(
                "Cache hit for job {JobId} scored {Score}/100 — re-enriching: {Missing}",
                jobId, quality.Score, string.Join("; ", quality.Missing));

            var enriched = await runner.ReEnrichAsync(job, baseResult, quality, Progress, timeout.Token);
            if (enriched is null)
            {
                return; // nothing could be improved — no UI update
            }

            job.ResultJson = enriched.ResultJson;
            await db.SaveChangesAsync(timeout.Token);
            await convos.CompleteAsync(
                userId, "completed", enriched.ResultJson, enriched.ResultType, DateTimeOffset.UtcNow, timeout.Token);
            await _broadcaster.EnrichedAsync(userId, jobId, enriched.ResultJson, enriched.ResultType);
        }
        catch (OperationCanceledException)
        {
            // Timed out — the cached result is already on screen so there's nothing to undo, but log it so an
            // abandoned partial re-enrichment (the gaps the validator flagged stay unfilled) is diagnosable.
            _logger.LogWarning(
                "Background cache re-enrichment for job {JobId} timed out after {TimeoutSeconds:n0}s and was abandoned; " +
                "the cached result stands but the flagged gaps were not refilled. Raise Enrichment:TimeoutSeconds if this recurs.",
                jobId, _enrichTimeout.TotalSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Background cache re-enrichment failed for job {JobId}", jobId);
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
        await convos.CompleteAsync(job.UserId, convoStatus, null, null, job.CompletedAt.Value, ct);
    }
}
