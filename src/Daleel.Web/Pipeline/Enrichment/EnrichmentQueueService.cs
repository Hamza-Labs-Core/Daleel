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
/// own DI scope with its own metering and its own wall-clock budget, and records the outcome. There
/// is deliberately NO job- or phase-level timeout — a slow unit retries alone, a dead container's
/// claims lease-expire back to pending, and completed work is already persisted by the handlers'
/// patches, so nothing is ever abandoned wholesale.
/// </summary>
public sealed class EnrichmentQueueService : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Claim lease — must exceed every handler's Budget so a live execution is never re-claimed.
    /// Lease expiry is the crash-recovery path, not a working-time limit.
    /// </summary>
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(10);

    private readonly IEnrichmentWorkQueue _queue;
    private readonly IEnrichedResultStore _results;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ICacheStore _cache;
    private readonly ILogger<EnrichmentQueueService> _logger;

    private readonly ConcurrentDictionary<long, Task> _running = new();

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
        while (!stoppingToken.IsCancellationRequested)
        {
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

        // The per-job cost cap is CUMULATIVE across the base run and every enrichment unit: this
        // unit may only spend what the job hasn't already. A tripped/exhausted cap kills the unit
        // visibly — a queued deep-dive must never grant the job a fresh budget.
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
        budget.CancelAfter(handler.Budget);

        var collector = new JobApiCallCollector(
            line => _logger.LogInformation(
                "Enrich unit {ItemId} ({Kind}, job {JobId}) API call · {Detail}",
                item.Id, item.Kind, item.SearchJobId, line),
            unitCap, capTrip);
        using var ambient = AmbientApiObserver.Begin(collector, estimator);

        var language = string.IsNullOrWhiteSpace(job.Language) ? "en" : job.Language;
        AgentService? agent = null;
        var ctx = new EnrichmentUnitContext
        {
            Services = sp,
            Job = job,
            Agent = () => agent ??= sp.GetRequiredService<IAgentFactory>().Build(new AgentRequest
            {
                Geo = job.Geo,
                Model = string.IsNullOrWhiteSpace(job.Model) ? null : job.Model,
                Language = language,
                Log = message => _logger.LogDebug("Enrich unit {ItemId}: {Message}", item.Id, message),
                ApiObserver = collector,
                CostEstimator = estimator,
                Cache = _cache,
                CacheTtl = TimeSpan.FromDays(30)
            }),
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
            outcome = new UnitOutcome.Retry($"unit budget exceeded ({handler.Budget.TotalSeconds:n0}s)");
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
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Enrich unit {ItemId}: failed to persist spend telemetry", item.Id);
        }
    }
}
