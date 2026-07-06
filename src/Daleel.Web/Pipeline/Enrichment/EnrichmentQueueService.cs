using System.Collections.Concurrent;
using Daleel.Agent;
using Daleel.Core.Caching;
using Daleel.Core.Observability;
using Daleel.Web.Conversation;
using Daleel.Web.Data;
using Daleel.Web.Events;
using Daleel.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace Daleel.Web.Pipeline.Enrichment;

/// <summary>
/// The enrichment queue consumer: claims work items, dispatches each to its kind's handler in its
/// own DI scope with its own metering, and records the outcome. The ONLY attempt bound is derived
/// from the claim lease (an attempt must end before its lease can expire, or another consumer could
/// claim the unit mid-run); there is deliberately NO job- or phase-level timeout — a slow unit
/// retries alone, a dead container's claims lease-expire back to pending, and completed work is
/// already persisted by the handlers' patches, so nothing is ever abandoned wholesale.
/// </summary>
public sealed class EnrichmentQueueService : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Claim lease — the system's ONE liveness number: crash recovery (an expired lease re-queues
    /// the unit) and, minus a write margin, the per-attempt bound derived from it.
    /// </summary>
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(10);

    /// <summary>How often to Dead lease-expired, attempts-exhausted rows (crash victims).</summary>
    private static readonly TimeSpan ReapInterval = TimeSpan.FromMinutes(2);

    private readonly IEnrichmentWorkQueue _queue;
    private readonly IEnrichedResultStore _results;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ICacheStore _cache;
    private readonly ILogger<EnrichmentQueueService> _logger;

    private readonly ConcurrentDictionary<long, Task> _running = new();

    // Per-job serialization gate, used ONLY when a cost cap is active for the job. Without it, N
    // units of one job each read the same spent-sum at start and are each granted the full remaining
    // budget — the job overshoots the admin ceiling by ~concurrency×. Holding this gate across a
    // capped unit makes the spent-sum read authoritative (within this process; cross-container
    // overshoot is bounded to one unit per container, and the cap is opt-in). Ref-counted so the
    // dictionary never grows past the set of in-flight capped jobs.
    private readonly Dictionary<int, (SemaphoreSlim Sem, int Refs)> _capGates = new();
    private readonly object _capGatesLock = new();

    private SemaphoreSlim AcquireCapGate(int jobId)
    {
        lock (_capGatesLock)
        {
            if (_capGates.TryGetValue(jobId, out var e))
            {
                _capGates[jobId] = (e.Sem, e.Refs + 1);
                return e.Sem;
            }

            var sem = new SemaphoreSlim(1, 1);
            _capGates[jobId] = (sem, 1);
            return sem;
        }
    }

    private void ReleaseCapGate(int jobId)
    {
        lock (_capGatesLock)
        {
            if (!_capGates.TryGetValue(jobId, out var e))
            {
                return;
            }

            if (e.Refs <= 1)
            {
                _capGates.Remove(jobId);
                e.Sem.Dispose();
            }
            else
            {
                _capGates[jobId] = (e.Sem, e.Refs - 1);
            }
        }
    }

    public EnrichmentQueueService(
        IEnrichmentWorkQueue queue, IEnrichedResultStore results, IServiceScopeFactory scopeFactory,
        ICacheStore cache, ILogger<EnrichmentQueueService> logger)
    {
        _queue = queue;
        _results = results;
        _scopeFactory = scopeFactory;
        _cache = cache;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Enrichment queue consumer started (concurrency {Concurrency})", PipelineLimits.EnrichmentConcurrency);
        using var timer = new PeriodicTimer(TickInterval);
        var lastReap = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            // Reap units that crashed the container before writing an outcome (lease expired,
            // attempts exhausted) — the claim query already refuses them, this makes them Dead and
            // visible. Cheap UPDATE, throttled so it's not run every 2s tick.
            if (DateTimeOffset.UtcNow - lastReap > ReapInterval)
            {
                lastReap = DateTimeOffset.UtcNow;
                try
                {
                    var reaped = await _queue.ReapExhaustedAsync(stoppingToken);
                    if (reaped > 0)
                    {
                        _logger.LogWarning("Reaped {Count} exhausted enrichment unit(s) as dead", reaped);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) { _logger.LogWarning(ex, "Enrichment reap tick failed"); }
            }

            var free = PipelineLimits.EnrichmentConcurrency - _running.Count;
            var claimedFull = false;
            if (free > 0)
            {
                try
                {
                    var claimed = await _queue.ClaimAsync(free, Lease, stoppingToken);
                    claimedFull = claimed.Count == free;
                    foreach (var item in claimed)
                    {
                        var task = Task.Run(() => ExecuteOneAsync(item, stoppingToken), CancellationToken.None);
                        _running[item.Id] = task;
                        _ = task.ContinueWith(
                            _ => _running.TryRemove(item.Id, out Task? _), CancellationToken.None);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Enrichment claim tick failed");
                }
            }

            // A full claim means more work is probably waiting — go straight back for it.
            if (claimedFull)
            {
                continue;
            }

            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        // Bounded drain: give in-flight units a moment to record their outcome; anything still
        // running after this simply lease-expires and re-runs — retries are idempotent by design.
        var drain = _running.Values.ToArray();
        if (drain.Length > 0)
        {
            await Task.WhenAny(Task.WhenAll(drain), Task.Delay(TimeSpan.FromSeconds(10), CancellationToken.None));
        }
    }

    private async Task ExecuteOneAsync(EnrichmentWorkItem item, CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;

        var handler = sp.GetServices<IEnrichmentUnitHandler>().FirstOrDefault(h => h.Kind == item.Kind);
        if (handler is null)
        {
            // Poison visibility: an unknown kind must die loudly, never loop.
            await _queue.KillAsync(item.Id, $"no handler for kind '{item.Kind}'");
            return;
        }

        var db = sp.GetRequiredService<DaleelDbContext>();
        var job = await db.SearchJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == item.SearchJobId, stoppingToken);
        if (job is null)
        {
            await _queue.KillAsync(item.Id, "search job no longer exists");
            return;
        }

        if (job.CancelRequested || job.Status == JobStatus.Cancelled)
        {
            await _queue.KillAsync(item.Id, "search job was cancelled");
            return;
        }

        var config = sp.GetRequiredService<ISystemConfigService>();
        var estimator = await CostConfig.BuildEstimatorAsync(config, stoppingToken);
        var caps = await CostConfig.ReadCapsAsync(config, stoppingToken);

        // When a cap is active, serialize this job's units so the cumulative-spend read below is
        // authoritative — concurrent units must not each be granted the full remaining budget.
        SemaphoreSlim? capGate = null;
        if (caps.MaxPerJob > 0)
        {
            capGate = AcquireCapGate(item.SearchJobId);
            try
            {
                await capGate.WaitAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                ReleaseCapGate(item.SearchJobId);
                return; // host shutdown — the row stays leased and re-runs
            }
        }

        try
        {
            await RunUnitAsync(item, sp, db, job, config, estimator, caps, handler, stoppingToken);
        }
        finally
        {
            if (capGate is not null)
            {
                capGate.Release();
                ReleaseCapGate(item.SearchJobId);
            }
        }
    }

    private async Task RunUnitAsync(
        EnrichmentWorkItem item, IServiceProvider sp, DaleelDbContext db, SearchJob job,
        ISystemConfigService config, Daleel.Core.Observability.CostEstimator estimator,
        CostCaps caps, IEnrichmentUnitHandler handler, CancellationToken stoppingToken)
    {
        // The per-job cost cap is CUMULATIVE across the base run and every enrichment unit: this
        // unit may only spend what the job hasn't already. A tripped/exhausted cap kills the unit
        // visibly — a queued deep-dive must never grant the job a fresh budget. Under the cap gate
        // above, this read reflects every already-committed unit's spend.
        var unitCap = caps.MaxPerJob;
        if (caps.MaxPerJob > 0)
        {
            var spent = await db.ApiCallLogs.Where(c => c.JobId == item.SearchJobId)
                .SumAsync(c => (decimal?)c.EstimatedCost, stoppingToken) ?? 0m;
            unitCap = caps.MaxPerJob - spent;
            if (unitCap <= 0)
            {
                await _queue.KillAsync(item.Id, $"per-job cost cap reached (spent {spent:F4})");
                return;
            }
        }

        using var capTrip = new CancellationTokenSource();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, capTrip.Token);
        // The ONLY attempt bound, DERIVED, never invented: an attempt must finish before its lease
        // can expire, or a second consumer could claim the unit while this one still runs it. The
        // margin covers the outcome/spend writes after the handler returns.
        budget.CancelAfter(Lease - TimeSpan.FromMinutes(1));

        var collector = new JobApiCallCollector(
            line => _logger.LogInformation(
                "Enrich unit {ItemId} ({Kind}, job {JobId}) API call · {Detail}",
                item.Id, item.Kind, item.SearchJobId, line),
            unitCap, capTrip);
        using var ambient = AmbientApiObserver.Begin(collector, estimator);

        var language = string.IsNullOrWhiteSpace(job.Language) ? "en" : job.Language;

        // One agent per distinct model, all wired to THIS unit's cost collector so every LLM call is
        // billed the same regardless of model. The actor loops pin a capable model (they can't run their
        // JSON reason-act loop on the weak free-tier default); everything else uses the job's model.
        var agents = new Dictionary<string, AgentService>(StringComparer.Ordinal);
        AgentService BuildAgent(string? modelOverride)
        {
            var model = string.IsNullOrWhiteSpace(modelOverride)
                ? (string.IsNullOrWhiteSpace(job.Model) ? null : job.Model)
                : modelOverride;
            var key = model ?? string.Empty;
            if (!agents.TryGetValue(key, out var built))
            {
                built = sp.GetRequiredService<IAgentFactory>().Build(new AgentRequest
                {
                    Geo = job.Geo,
                    Model = model,
                    Language = language,
                    Log = message => _logger.LogDebug("Enrich unit {ItemId}: {Message}", item.Id, message),
                    ApiObserver = collector,
                    CostEstimator = estimator,
                    Cache = _cache,
                    CacheTtl = TimeSpan.FromDays(30)
                });
                agents[key] = built;
            }

            return built;
        }

        var ctx = new EnrichmentUnitContext
        {
            Services = sp,
            Job = job,
            Agent = () => BuildAgent(null),
            AgentForModel = BuildAgent,
            Results = _results,
            Queue = _queue
        };

        UnitOutcome outcome;
        try
        {
            outcome = await handler.ExecuteAsync(item, ctx, budget.Token);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return; // host shutdown — leave the row leased; expiry re-queues it untouched
        }
        catch (OperationCanceledException) when (capTrip.IsCancellationRequested)
        {
            outcome = new UnitOutcome.Kill("per-job cost cap tripped mid-unit");
        }
        catch (OperationCanceledException)
        {
            outcome = new UnitOutcome.Retry("attempt outlived its lease bound");
        }
        catch (Exception ex)
        {
            outcome = new UnitOutcome.Retry(ex.Message);
        }
        finally
        {
            await PersistSpendAsync(sp, item, collector);
        }

        // Outcome writes use CancellationToken.None: mid-shutdown, losing the status update would
        // re-run a finished unit (safe but wasteful); recording it is one cheap UPDATE.
        switch (outcome)
        {
            case UnitOutcome.Done:
                await _queue.CompleteAsync(item.Id);
                break;
            case UnitOutcome.Retry retry:
                _logger.LogInformation(
                    "Enrich unit {ItemId} ({Kind}, job {JobId}) attempt {Attempt}/{Max} will retry: {Reason}",
                    item.Id, item.Kind, item.SearchJobId, item.Attempts, item.MaxAttempts, retry.Reason);
                await _queue.RetryAsync(item.Id, retry.Reason, retry.Delay);
                break;
            case UnitOutcome.Kill kill:
                _logger.LogWarning(
                    "Enrich unit {ItemId} ({Kind}, job {JobId}) killed: {Reason}",
                    item.Id, item.Kind, item.SearchJobId, kill.Reason);
                await _queue.KillAsync(item.Id, kill.Reason);
                break;
        }
    }

    /// <summary>
    /// Records this execution's provider calls exactly like a base run does (ApiCallLogs + the
    /// event firehose), so unit spend lands in the same per-job ledger and dashboards.
    /// </summary>
    private async Task PersistSpendAsync(IServiceProvider sp, EnrichmentWorkItem item, JobApiCallCollector collector)
    {
        var calls = collector.Calls;
        if (calls.Count == 0)
        {
            return;
        }

        try
        {
            var hashedUser = Anonymizer.HashUserId(item.UserId);
            var rows = calls.Select(c => new ApiCallLog
            {
                UserId = hashedUser, JobId = item.SearchJobId, Provider = c.Provider, Endpoint = c.Endpoint,
                RequestSummary = c.RequestSummary, ResponseTimeMs = c.ResponseTimeMs, ResponseBytes = c.ResponseBytes,
                Status = c.Status.ToString().ToLowerInvariant(), EstimatedCost = c.EstimatedCost,
                Model = c.Model, InputTokens = c.InputTokens, OutputTokens = c.OutputTokens, CreatedAt = c.Timestamp
            });
            await sp.GetRequiredService<IApiCallLogRepository>().AddBatchAsync(rows, CancellationToken.None);

            var eventStore = sp.GetRequiredService<IEventStore>();
            if (eventStore.IsEnabled)
            {
                var searchId = item.SearchJobId.ToString();
                await eventStore.RecordBatchAsync(
                    calls.Select(c => PipelineEventFactory.FromApiCall(c, searchId)).ToList(),
                    CancellationToken.None);
            }

            // Charge the unit's credits to the user, exactly as the base run charges its own — the
            // queue's recursive deep-dive spend (item dives, catalogue crawls, image lookups) is
            // real work the user must pay for, not free enrichment. Atomic increment (QuotaService),
            // so concurrent units don't race; UserId here is the raw id the base run also uses.
            var credits = collector.TotalCredits;
            if (credits > 0)
            {
                await sp.GetRequiredService<IQuotaService>()
                    .ChargeCreditsAsync(item.UserId, credits, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Enrich unit {ItemId}: failed to persist spend telemetry", item.Id);
        }
    }
}
