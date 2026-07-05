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
/// <para>
/// DELIBERATE TRADEOFF (don't "fix" without intent): this is a flat <see cref="Sequence"/> of
/// unconditional <c>CodeActivity</c> nodes that self-skip via the shared <c>state.FromCache</c> flag,
/// rather than modelling the cache-hit as an Elsa <c>If</c> edge or the fan-out as a declarative
/// <c>ForEach</c>/parallel activity. The pipeline is intentionally linear, and keeping the control flow
/// in plain C# (the <c>if (state.FromCache) return;</c> guards and the <c>Task.WhenAll</c> inside the
/// dispatch activities) keeps each stage readable and unit-testable in isolation. Elsa here earns its
/// keep as the activity-registration + sequencing + per-step telemetry seam, NOT as a
/// declarative-control-flow engine. If the pipeline ever grows real branching, revisit this: either lean
/// into Elsa's <c>If</c>/<c>Flowchart</c>/<c>ForEach</c>, or drop Elsa for a plain orchestrator — but do
/// it as a conscious migration, not a piecemeal mix.
/// </para>
/// </remarks>
public sealed class SearchWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Root = new Sequence
        {
            Activities =
            {
                new ParseQueryActivity(),                  // 1. normalize + plan
                new CheckCacheActivity(),                  // 2. replay cached report (short-circuits the rest)
                new AnalyzeMarketActivity(),               // 2b. think: category type, stores, brands, comparison specs
                new GatherSourcesActivity(),               // 3. fan out to providers
                new ExtractProductsActivity(),             // 4. LLM analyst + product extraction → FIRST partial to the UI
                new DispatchEnrichmentWorkflowsActivity(), // 5–7. brand/store/item sub-workflows CONCURRENTLY, streaming partials
                new AggregateResultsActivity(),            // 8. assemble the ranked answer
                new ModerateContentActivity(),             // 9. record halal-filter audit
                new CacheResultsActivity(),                // 10. serialize + persist
                new ReturnResultsActivity()                // 11. finalize
            }
        };
    }
}
