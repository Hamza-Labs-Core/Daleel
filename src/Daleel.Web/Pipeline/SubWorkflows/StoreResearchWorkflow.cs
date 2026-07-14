using Elsa.Workflows;
using Elsa.Workflows.Activities;

namespace Daleel.Web.Pipeline.SubWorkflows;

/// <summary>
/// Per-store research sub-workflow, dispatched in parallel (one child per store) from the main
/// <see cref="SearchWorkflow"/>. It scrapes the store's site, verifies it on Google Maps, extracts
/// contact info, persists the verified profile, and harvests catalogue prices — operating on a scoped
/// <see cref="StoreResearchState"/> the dispatcher seeds and reads back.
/// </summary>
public sealed class StoreResearchWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Root = new Sequence
        {
            Activities =
            {
                new ScrapeStoreSiteActivity(),     // 1. listings + contact via Context.dev (DB-first)
                new VerifyOnMapsActivity(),        // 2. Google Maps location/rating/hours
                new ExtractContactInfoActivity(),  // 3. phone + address onto the result
                new SaveStoreProfileActivity(),    // 4. persist the verified profile (finalizes the site URL)
                new CrawlStoreSiteActivity(),      // 5. LLM-crawl the store's site (replaces the single-page fetch)
                new ScrapePricesActivity()         // 6. single-page catalogue prices — FALLBACK when the crawl didn't run
            }
        };
    }
}
