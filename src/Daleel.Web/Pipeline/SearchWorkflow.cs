using Elsa.Workflows;
using Elsa.Workflows.Activities;

namespace Daleel.Web.Pipeline;

/// <summary>
/// The Daleel search pipeline expressed as an Elsa 3 workflow, replacing the ad-hoc orchestration
/// that <c>AgentService.AskAsync</c> + <c>AgentSearchRunner</c> performed by hand. The orchestrator runs
/// in sequence; each step reads/writes the scoped <see cref="SearchPipelineState"/>, and every post-cache
/// step no-ops when CheckCache served a stored report.
/// </summary>
/// <remarks>
/// The plan → gather → analyze/project stages still call the existing (now public) AgentService
/// methods, so behaviour is unchanged — Elsa supplies the orchestration, sequencing and observability,
/// not new business logic. Enrichment is no longer a flat loop: the three Dispatch* steps fan a
/// per-entity sub-workflow (brand / store / item) out in bounded parallel, each in its own DI scope.
/// </remarks>
public sealed class SearchWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Root = new Sequence
        {
            Activities =
            {
                new ParseQueryActivity(),                // 1. normalize + plan
                new CheckCacheActivity(),                // 2. replay cached report (short-circuits the rest)
                new GatherSourcesActivity(),             // 3. fan out to providers
                new ExtractProductsActivity(),           // 4. LLM analyst + product extraction
                new DispatchBrandWorkflowsActivity(),    // 5. one BrandResearchWorkflow per brand (parallel)
                new DispatchStoreWorkflowsActivity(),    // 6. one StoreResearchWorkflow per store (parallel)
                new DispatchItemWorkflowsActivity(),     // 7. one ItemDeepDiveWorkflow per model (parallel)
                new AggregateResultsActivity(),          // 8. assemble the ranked answer
                new ModerateContentActivity(),           // 9. record halal-filter audit
                new CacheResultsActivity(),              // 10. serialize + persist
                new ReturnResultsActivity()              // 11. finalize
            }
        };
    }
}
