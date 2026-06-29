using Daleel.Web.Conversation;
using Elsa.Extensions;
using Elsa.Workflows;

namespace Daleel.Web.Pipeline;

/// <summary>
/// Cooperative cancellation check for the search pipeline. Each activity calls this at the top of its
/// work; if the user has requested cancellation it throws, unwinding the run early so no further provider
/// cost is incurred. This is the best-effort fast layer — the worker's pre-commit re-check and the
/// periodic sweep (both reading the durable <c>SearchJob.CancelRequested</c> flag) guarantee the job
/// actually stops even when an activity never reaches this check or the engine swallows the throw.
/// </summary>
internal static class CancellationGuard
{
    /// <summary>
    /// Throws <see cref="OperationCanceledException"/> when a cancel has been requested for this run.
    /// No-op when the run has no job id (e.g. a test harness) or the flag isn't set.
    /// </summary>
    public static void ThrowIfCancelRequested(ActivityExecutionContext context)
    {
        var state = context.GetRequiredService<SearchPipelineState>();
        if (!int.TryParse(state.SearchId, out var jobId))
        {
            return;
        }

        // The queue is the same-process flag holder; resolved as optional so the pipeline still runs in
        // contexts where the queue isn't registered.
        var queue = context.GetService<ISearchJobQueue>();
        if (queue is not null && queue.IsCancelRequested(jobId))
        {
            throw new OperationCanceledException($"Search job {jobId} was cancelled by the user.");
        }
    }
}
