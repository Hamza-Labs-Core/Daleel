using Elsa.Workflows;
using Elsa.Workflows.Activities;

namespace Daleel.Web.Pipeline.SubWorkflows;

/// <summary>
/// Per-item deep-dive sub-workflow, dispatched in parallel (one child per product/model) from the main
/// <see cref="SearchWorkflow"/>. It scrapes the item's detail page, <em>identifies</em> which canonical
/// brand model the (often vaguely-named) listing actually is, then runs the smart-spec pipeline — save raw
/// specs per source, merge/clean/normalize them, and persist the canonical sheet — before comparing prices,
/// collecting reviews, and saving the enriched profile. Operates on a scoped <see cref="ItemDeepDiveState"/>
/// the dispatcher seeds and reads back.
/// </summary>
public sealed class ItemDeepDiveWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Root = new Sequence
        {
            Activities =
            {
                new ScrapeProductPagesActivity(),  // 1. fetch specs (DB-first reuse)
                new IdentifyProductActivity(),     // 2. text/vision match to the canonical brand model
                new ExtractSpecsActivity(),        // 3. fold the scraped detail into the model
                new SaveRawSpecsActivity(),        // 4. persist each source's raw specs + images to R2
                new MergeAndCleanSpecsActivity(),  // 5. dedupe, normalize units, resolve conflicts
                new SaveFinalSpecsActivity(),      // 6. persist the canonical sheet (DB + R2)
                new ComparePricesActivity(),       // 7. aggregate offers across stores
                new CollectReviewsActivity(),      // 8. record gathered reviews/ratings
                new SaveItemProfileActivity()      // 9. persist for reuse
            }
        };
    }
}
