using Elsa.Workflows;
using Elsa.Workflows.Activities;

namespace Daleel.Web.Pipeline.SubWorkflows;

/// <summary>
/// Per-item deep-dive sub-workflow, dispatched in parallel (one child per product/model) from the main
/// <see cref="SearchWorkflow"/>. It scrapes the item's detail page for specs (DB-first), compares its
/// store prices, collects reviews, and persists the enriched profile — operating on a scoped
/// <see cref="ItemDeepDiveState"/> the dispatcher seeds and reads back.
/// </summary>
public sealed class ItemDeepDiveWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Root = new Sequence
        {
            Activities =
            {
                new ScrapeProductPagesActivity(), // 1. fetch specs (DB-first reuse)
                new ExtractSpecsActivity(),       // 2. fold specs into the model
                new ComparePricesActivity(),      // 3. aggregate offers across stores
                new CollectReviewsActivity(),     // 4. record gathered reviews/ratings
                new SaveItemProfileActivity()     // 5. persist for reuse
            }
        };
    }
}
