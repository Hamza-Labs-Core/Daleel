using Daleel.Web.Conversation;
using Daleel.Web.Data;
using Daleel.Web.Pipeline.SubWorkflows;
using Elsa.Extensions;
using Elsa.Workflows;
using Microsoft.EntityFrameworkCore;

namespace Daleel.Web.Pipeline;

/// <summary>
/// Implemented by the run-scoped pipeline states (the main <see cref="SearchPipelineState"/> and every
/// per-entity <see cref="SubWorkflowState"/>) so the shared cancellation check can find the owning
/// <c>SearchJob</c> id no matter which workflow an activity belongs to.
/// </summary>
public interface ISearchScopedState
{
    /// <summary>The owning SearchJob id (as a string), or null outside a real run.</summary>
    string? SearchId { get; }
}

/// <summary>
/// Base class for every search-pipeline activity (main pipeline and sub-workflows). Its <b>sealed</b>
/// <see cref="ExecuteAsync"/> checks the owning job's cancel flag BEFORE the step runs and throws
/// <see cref="OperationCanceledException"/> when a cancel has been requested — so the check is structural
/// and no individual activity can forget or skip it. Derived activities put their work in
/// <see cref="DoExecuteAsync"/> instead of overriding <c>ExecuteAsync</c>.
///
/// The durable <c>SearchJob.CancelRequested</c> column is the source of truth (set the instant the user
/// cancels, visible across the worker / sweep / UI even though they use different DbContexts) and the ONLY
/// thing consulted here — there is no in-memory state. This is the cooperative layer — the worker's
/// pre-commit re-check and the periodic <see cref="JobReconciliationService"/> sweep are the hard backstops
/// that stop a run even if it never reaches a check here.
/// </summary>
public abstract class CancellableActivity : CodeActivity
{
    protected sealed override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        await ThrowIfCancelRequestedAsync(context).ConfigureAwait(false);
        await DoExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>The activity's actual work, run only after the cancel check passes.</summary>
    protected abstract ValueTask DoExecuteAsync(ActivityExecutionContext context);

    private static async ValueTask ThrowIfCancelRequestedAsync(ActivityExecutionContext context)
    {
        if (ResolveJobId(context) is not { } jobId)
        {
            return; // no real run (e.g. a unit-test harness) — nothing to cancel against
        }

        // The durable flag is the source of truth: it's set the instant the user hits Cancel and is visible
        // here whichever context wrote it (the UI's, or the periodic sweep's).
        //
        // This whole check is the BEST-EFFORT cooperative layer (the worker's pre-commit re-check and the
        // periodic JobReconciliationService sweep are the hard backstops). So a FAILED durable read must
        // never fault the run: a transient DB blip, pool exhaustion, or a not-yet-migrated CancelRequested
        // column on a freshly-deployed instance would otherwise throw out of the FIRST activity — before any
        // products exist to salvage — turning every search into a hard "no results". Degrade to "not
        // cancelled, keep running" on any non-cancellation failure; only a genuine cancel/deadline
        // (OperationCanceledException, from context.CancellationToken) propagates and stops the run.
        if (context.GetService<DaleelDbContext>() is { } db)
        {
            bool requested;
            try
            {
                requested = await db.SearchJobs
                    .Where(j => j.Id == jobId)
                    .Select(j => j.CancelRequested)
                    .FirstOrDefaultAsync(context.CancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw; // a real user cancel / cost-cap / workflow-deadline must stop the run
            }
            catch
            {
                return; // cooperative check is best-effort — a durable-read failure must not fault the search
            }

            if (requested)
            {
                throw Cancelled(jobId);
            }
        }
    }

    /// <summary>
    /// Finds the SearchJob id from whichever run-state is seeded in this activity's scope. Every state
    /// type is registered in the root container, so all are resolvable in any scope — but only the one
    /// belonging to this scope was seeded with a SearchId; the rest come back fresh (null id) and are
    /// skipped. The main state is tried first so a main-pipeline activity never resolves a sub-state.
    /// </summary>
    private static int? ResolveJobId(ActivityExecutionContext context) =>
        Try<SearchPipelineState>(context)
        ?? Try<BrandResearchState>(context)
        ?? Try<StoreResearchState>(context)
        ?? Try<ItemDeepDiveState>(context)
        ?? Try<StoreCrawlState>(context)
        ?? Try<BrandCrawlState>(context)
        ?? Try<ProductDetailState>(context);

    private static int? Try<T>(ActivityExecutionContext context) where T : class =>
        context.GetService<T>() is ISearchScopedState { SearchId: { } id } && int.TryParse(id, out var jobId)
            ? jobId
            : null;

    private static OperationCanceledException Cancelled(int jobId) =>
        new($"Search job {jobId} was cancelled by the user.");
}
