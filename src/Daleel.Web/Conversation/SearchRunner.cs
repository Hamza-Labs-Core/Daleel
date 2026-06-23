using Daleel.Web.Data;
using Daleel.Web.Services;

namespace Daleel.Web.Conversation;

/// <summary>The outcome of running a search job.</summary>
public sealed record SearchRunResult(string ResultJson, string ResultType, int FilteredCount, string FilteredCategories);

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

    public AgentSearchRunner(IAgentFactory agents) => _agents = agents;

    public async Task<SearchRunResult> RunAsync(SearchJob job, Action<string> progress, CancellationToken ct)
    {
        // Background jobs resolve keys from server env only (browser BYO keys aren't available here).
        var agent = _agents.Build(new AgentRequest
        {
            Geo = job.Geo,
            Model = string.IsNullOrWhiteSpace(job.Model) ? null : job.Model,
            Log = progress
        });

        var answer = await agent.AskAsync(job.Query, job.Geo, ct).ConfigureAwait(false);

        var audit = agent.ContentFilter.AuditLog;
        var categories = audit
            .Select(a => a.Contains(':') ? a[(a.IndexOf(':') + 1)..] : a)
            .Distinct();

        return new SearchRunResult(
            ResultSerialization.Serialize(answer),
            "ask",
            audit.Count,
            string.Join(",", categories));
    }
}
