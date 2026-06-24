using Daleel.Core.Observability;
using Daleel.Web.Data;
using Daleel.Web.Services;

namespace Daleel.Web.Conversation;

/// <summary>The outcome of running a search job.</summary>
public sealed record SearchRunResult(
    string ResultJson, string ResultType, int FilteredCount, string FilteredCategories,
    int ApiCalls = 0, decimal EstimatedCost = 0m);

/// <summary>
/// Runs the actual agent query for a job. Abstracted from <c>SearchJobService</c> so the worker can
/// be unit-tested with a fake runner (no real LLM/providers needed).
/// </summary>
public interface ISearchRunner
{
    Task<SearchRunResult> RunAsync(SearchJob job, Action<string> progress, CancellationToken ct);
}

/// <summary>Production runner: builds an agent from server-side keys and runs the unified ask flow.</summary>
public sealed class AgentSearchRunner : ISearchRunner
{
    private readonly IAgentFactory _agents;
    private readonly ISystemConfigService _config;
    private readonly IApiCallLogRepository _apiLog;

    public AgentSearchRunner(IAgentFactory agents, ISystemConfigService config, IApiCallLogRepository apiLog)
    {
        _agents = agents;
        _config = config;
        _apiLog = apiLog;
    }

    public async Task<SearchRunResult> RunAsync(SearchJob job, Action<string> progress, CancellationToken ct)
    {
        // Per-job cost instrumentation: estimate + cap from admin config, stream each call live.
        var estimator = await CostConfig.BuildEstimatorAsync(_config, ct).ConfigureAwait(false);
        var caps = await CostConfig.ReadCapsAsync(_config, ct).ConfigureAwait(false);

        using var capTrip = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var collector = new JobApiCallCollector(progress, caps.MaxPerJob, capTrip);

        // Background jobs resolve keys from server env only (browser BYO keys aren't available here).
        var agent = _agents.Build(new AgentRequest
        {
            Geo = job.Geo,
            Model = string.IsNullOrWhiteSpace(job.Model) ? null : job.Model,
            Language = string.IsNullOrWhiteSpace(job.Language) ? "en" : job.Language,
            Log = progress,
            ApiObserver = collector,
            CostEstimator = estimator
        });

        try
        {
            var answer = await agent.AskAsync(job.Query, job.Geo, capTrip.Token).ConfigureAwait(false);

            var audit = agent.ContentFilter.AuditLog;
            var categories = audit
                .Select(a => a.Contains(':') ? a[(a.IndexOf(':') + 1)..] : a)
                .Distinct();

            return new SearchRunResult(
                ResultSerialization.Serialize(answer),
                "ask",
                audit.Count,
                string.Join(",", categories),
                collector.Calls.Count,
                collector.TotalCost);
        }
        finally
        {
            // Persist the call log regardless of outcome (success, cap-trip, or error) so usage
            // and cost are always recorded.
            await PersistAsync(job, collector, ct).ConfigureAwait(false);
        }
    }

    private async Task PersistAsync(SearchJob job, JobApiCallCollector collector, CancellationToken ct)
    {
        var calls = collector.Calls;
        if (calls.Count == 0)
        {
            return;
        }

        var rows = calls.Select(c => new ApiCallLog
        {
            UserId = job.UserId,
            JobId = job.Id,
            Provider = c.Provider,
            Endpoint = c.Endpoint,
            RequestSummary = c.RequestSummary,
            ResponseTimeMs = c.ResponseTimeMs,
            ResponseBytes = c.ResponseBytes,
            Status = c.Status.ToString().ToLowerInvariant(),
            EstimatedCost = c.EstimatedCost,
            Model = c.Model,
            InputTokens = c.InputTokens,
            OutputTokens = c.OutputTokens,
            CreatedAt = c.Timestamp
        });

        // Don't let a logging failure mask the job's real outcome; and persist even if the job was cancelled.
        try
        {
            await _apiLog.AddBatchAsync(rows, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // best-effort
        }
    }
}
