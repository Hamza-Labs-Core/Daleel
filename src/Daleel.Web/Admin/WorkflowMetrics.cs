using Daleel.Agent;
using Daleel.Web.Data;
using Daleel.Web.Services;

namespace Daleel.Web.Admin;

/// <summary>
/// Pure, DB-free helpers behind the admin Workflows page: deriving a search run's result count from its
/// stored JSON and its elapsed duration from the job timestamps. Kept here (not inline in the .razor) so
/// the logic is unit-tested without rendering a component.
/// </summary>
public static class WorkflowMetrics
{
    /// <summary>
    /// Number of product models a completed run surfaced, parsed from <see cref="SearchJob.ResultJson"/>.
    /// Returns null for non-product answers (a free-form "ask" has no countable items) or unparseable JSON.
    /// </summary>
    public static int? ResultCount(string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson))
        {
            return null;
        }

        try
        {
            var answer = ResultSerialization.Deserialize<AgentAnswer>(resultJson);
            return answer?.Products?.ProductCount;
        }
        catch
        {
            // A result shape we don't recognize just has no item count — never throw into the page.
            return null;
        }
    }

    /// <summary>
    /// Wall-clock duration of a job. Uses StartedAt→CompletedAt when both are set; for a still-running job
    /// (no CompletedAt) measures against <paramref name="now"/>. Null when it never started.
    /// </summary>
    public static TimeSpan? Duration(SearchJob job, DateTimeOffset now)
    {
        if (job.StartedAt is not { } started)
        {
            return null;
        }

        var end = job.CompletedAt ?? now;
        var elapsed = end - started;
        // Clock skew between writers could yield a tiny negative span; floor at zero.
        return elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
    }
}
