using Elsa.Workflows;
using Elsa.Workflows.Activities;

namespace Daleel.Web.Pipeline;

/// <summary>
/// The Daleel search pipeline expressed as an Elsa 3 workflow, replacing the ad-hoc orchestration
/// that <c>AgentService.AskAsync</c> + <c>AgentSearchRunner</c> performed by hand. The nine steps run
/// in sequence; each reads/writes the scoped <see cref="SearchPipelineState"/>, and every post-cache
/// step no-ops when CheckCache served a stored report.
/// </summary>
/// <remarks>
/// The plan → gather → analyze/project stages still call the existing (now public) AgentService
/// methods, so behaviour is unchanged — Elsa supplies the orchestration, sequencing and observability,
/// not new business logic.
/// </remarks>
public sealed class SearchWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Root = new Sequence
        {
            Activities =
            {
                new ParseQueryActivity(),         // 1. normalize + plan
                new CheckCacheActivity(),          // 2. replay cached report (short-circuits the rest)
                new GatherSourcesActivity(),       // 3. fan out to providers
                new ExtractProductsActivity(),     // 4. LLM analyst + product extraction
                new EnrichWithProfilesActivity(),  // 5. join saved Brand/Store profiles
                new ItemDeepDiveActivity(),        // 5b. per-item: compare store prices + scrape specs
                new AggregateResultsActivity(),    // 6. assemble the ranked answer
                new ModerateContentActivity(),     // 7. record halal-filter audit
                new CacheResultsActivity(),        // 8. serialize + persist
                new ReturnResultsActivity()        // 9. finalize
            }
        };
    }
}
