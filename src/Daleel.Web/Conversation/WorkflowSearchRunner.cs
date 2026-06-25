using Daleel.Core.Caching;
using Daleel.Core.Moderation;
using Daleel.Core.Observability;
using Daleel.Web.Data;
using Daleel.Web.Events;
using Daleel.Web.Pipeline;
using Daleel.Web.Services;
using Elsa.Workflows;

namespace Daleel.Web.Conversation;

/// <summary>
/// Production <see cref="ISearchRunner"/> that drives the search through the Elsa
/// <see cref="SearchWorkflow"/> instead of calling <c>AgentService.AskAsync</c> directly. It still
/// owns the request-level concerns the workflow shouldn't: building the agent from server keys, cost
/// instrumentation/caps, and persisting the API-call + content-filter audit logs. The plan → gather →
/// analyze → enrich → aggregate → moderate → cache orchestration now lives in the workflow.
/// </summary>
public sealed class WorkflowSearchRunner : ISearchRunner
{
    /// <summary>TTL for both cache layers — a cached search stays valid for 30 days.</summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(30);

    private readonly IAgentFactory _agents;
    private readonly ISystemConfigService _config;
    private readonly IApiCallLogRepository _apiLog;
    private readonly IFilteredContentLogRepository _filteredLog;
    private readonly ICacheStore _cache;
    private readonly IEventStore _eventStore;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<WorkflowSearchRunner> _logger;

    public WorkflowSearchRunner(
        IAgentFactory agents, ISystemConfigService config, IApiCallLogRepository apiLog,
        IFilteredContentLogRepository filteredLog, ICacheStore cache, IEventStore eventStore,
        IServiceScopeFactory scopeFactory, ILogger<WorkflowSearchRunner> logger)
    {
        _agents = agents;
        _config = config;
        _apiLog = apiLog;
        _filteredLog = filteredLog;
        _cache = cache;
        _eventStore = eventStore;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<SearchRunResult> RunAsync(SearchJob job, Action<string> progress, CancellationToken ct)
    {
        var language = string.IsNullOrWhiteSpace(job.Language) ? "en" : job.Language;
        var resultKey = CacheKey.ForResult(job.Query, job.Geo, language);

        // Per-job cost instrumentation: estimate + cap from admin config, stream each call live.
        var estimator = await CostConfig.BuildEstimatorAsync(_config, ct).ConfigureAwait(false);
        var caps = await CostConfig.ReadCapsAsync(_config, ct).ConfigureAwait(false);

        using var capTrip = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var collector = new JobApiCallCollector(
            line => _logger.LogInformation("Search job {JobId} API call · {Detail}", job.Id, line),
            caps.MaxPerJob, capTrip);

        // Background jobs resolve keys from server env only (browser BYO keys aren't available here).
        var agent = _agents.Build(new AgentRequest
        {
            Geo = job.Geo,
            Model = string.IsNullOrWhiteSpace(job.Model) ? null : job.Model,
            Language = language,
            Log = progress,
            ApiObserver = collector,
            CostEstimator = estimator,
            Cache = _cache,
            CacheTtl = CacheTtl
        });

        // Captured from the run's state so the finally block can flush buffered cache/profile events
        // even when the workflow faults partway through.
        IReadOnlyList<PipelineEvent> bufferedEvents = Array.Empty<PipelineEvent>();

        try
        {
            // One scope per run so the per-run SearchPipelineState (scoped) is isolated; the Elsa
            // activities resolve that same instance from their execution context.
            using var scope = _scopeFactory.CreateScope();
            var state = scope.ServiceProvider.GetRequiredService<SearchPipelineState>();
            state.Agent = agent;
            state.Query = job.Query;
            state.Geo = job.Geo;
            state.Language = language;
            state.ResultKey = resultKey;
            state.Cache = _cache;
            state.CacheTtl = CacheTtl;
            state.Progress = progress;
            state.SearchId = job.Id.ToString();
            bufferedEvents = state.Events;

            var runner = scope.ServiceProvider.GetRequiredService<IWorkflowRunner>();
            var run = await runner.RunAsync(new SearchWorkflow(), cancellationToken: capTrip.Token).ConfigureAwait(false);

            // A faulted/cancelled run reports Status == Finished but a non-Finished SubStatus, leaving
            // ResultJson empty or partial. Surface that as a failure instead of returning a broken result.
            if (run.WorkflowState.SubStatus != WorkflowSubStatus.Finished)
            {
                var reason = string.Join("; ", run.WorkflowState.Incidents.Select(i => i.Message));
                throw new InvalidOperationException(
                    $"Search workflow did not finish (sub-status: {run.WorkflowState.SubStatus})" +
                    (string.IsNullOrWhiteSpace(reason) ? "." : $": {reason}"));
            }

            await RecordResultCacheAsync(job, state.FromCache ? "hit" : "miss", ct).ConfigureAwait(false);

            var providers = string.Join(",", collector.Calls
                .Select(c => c.Provider)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase));

            return new SearchRunResult(
                state.ResultJson,
                state.ResultType,
                state.FilteredCount,
                state.FilteredCategories,
                collector.Calls.Count,
                collector.TotalCost,
                state.ResultCount,
                providers);
        }
        finally
        {
            await PersistAsync(job, collector, ct).ConfigureAwait(false);
            await PersistFilteredAsync(job, agent.ContentFilter.AuditDetails, ct).ConfigureAwait(false);
            await PersistEventsAsync(job, collector, bufferedEvents, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Flushes the run's pipeline events to the (optional) Postgres event store: every provider call
    /// (projected from the API-call collector) plus the buffered cache/profile events. No-op + cheap
    /// when the event store is the null/unconfigured one.
    /// </summary>
    private async Task PersistEventsAsync(
        SearchJob job, JobApiCallCollector collector, IReadOnlyList<PipelineEvent> buffered, CancellationToken ct)
    {
        if (!_eventStore.IsEnabled)
        {
            return;
        }

        var searchId = job.Id.ToString();
        var events = new List<PipelineEvent>(collector.Calls.Count + buffered.Count);
        events.AddRange(collector.Calls.Select(c => PipelineEventFactory.FromApiCall(c, searchId)));
        events.AddRange(buffered);

        if (events.Count > 0)
        {
            await _eventStore.RecordBatchAsync(events, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task PersistFilteredAsync(
        SearchJob job, IReadOnlyList<ContentFilter.FilterAudit> details, CancellationToken ct)
    {
        if (details.Count == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var rows = details.Select(d => new FilteredContentLog
        {
            Query = job.Query, Geo = job.Geo, Category = d.Category, Rule = d.Term,
            Kind = d.Kind, Content = d.Content, CreatedAt = now
        });

        try
        {
            await _filteredLog.AddBatchAsync(rows, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // best-effort: never let audit logging affect the search outcome
        }
    }

    private async Task PersistAsync(SearchJob job, JobApiCallCollector collector, CancellationToken ct)
    {
        var calls = collector.Calls;
        if (calls.Count == 0)
        {
            return;
        }

        var hashedUser = Anonymizer.HashUserId(job.UserId);
        var rows = calls.Select(c => new ApiCallLog
        {
            UserId = hashedUser, JobId = job.Id, Provider = c.Provider, Endpoint = c.Endpoint,
            RequestSummary = c.RequestSummary, ResponseTimeMs = c.ResponseTimeMs, ResponseBytes = c.ResponseBytes,
            Status = c.Status.ToString().ToLowerInvariant(), EstimatedCost = c.EstimatedCost,
            Model = c.Model, InputTokens = c.InputTokens, OutputTokens = c.OutputTokens, CreatedAt = c.Timestamp
        });

        try
        {
            await _apiLog.AddBatchAsync(rows, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // best-effort
        }
    }

    private async Task RecordResultCacheAsync(SearchJob job, string outcome, CancellationToken ct)
    {
        var row = new ApiCallLog
        {
            UserId = Anonymizer.HashUserId(job.UserId), JobId = job.Id, Provider = "cache",
            Endpoint = $"result/{outcome}", RequestSummary = RequestSummaries.Truncate(job.Query),
            Status = "success", EstimatedCost = 0m, CreatedAt = DateTimeOffset.UtcNow
        };

        try
        {
            await _apiLog.AddBatchAsync(new[] { row }, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // best-effort: cache telemetry must never affect the search outcome
        }
    }
}
