using Elsa.Workflows;
using Elsa.Workflows.Activities;

namespace Daleel.Web.Pipeline.SubWorkflows;

/// <summary>
/// The LLM crawler specialised for e-commerce STORES (taghareedstore.com, jo-cell.com …). Given a store URL
/// and the shopper's query it uses the LLM to navigate commerce-style — assess the platform, use site search
/// or a category page or the product API (Shopify <c>/products.json</c>), walk the paginated product listings
/// extracting price/stock/SKU — then deep-dives the top matches via <see cref="ProductDetailWorkflow"/> and
/// persists everything to R2 + the index + the price series.
/// </summary>
/// <remarks>
/// Dispatched per-store from store research (replacing the single-page catalogue fetch) via
/// <see cref="CrawlDispatch"/>, operating on a scoped <see cref="StoreCrawlState"/>. Every page renders
/// through the METERED scraper (<c>AgentService.ReadPageAsync</c>) — never a direct provider — is archived to
/// R2, and every LLM decision is logged to the event spine. Bounded by <c>PipelineLimits.CrawlMaxPages</c>/
/// <c>CrawlMaxDeepDive</c>, the per-entity timeout, and the 10-minute deadline. Flat <see cref="Sequence"/>
/// per the pipeline convention (loop/fan-out in plain C#, not Elsa control-flow nodes).
/// </remarks>
public sealed class StoreCrawlWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Root = new Sequence
        {
            Activities =
            {
                new AssessStoreActivity(),          // 1. render homepage → LLM: platform + entry points
                new FindStoreListingsActivity(),    // 2. resolve the approach → concrete listing URL
                new ExtractStoreListingActivity(),  // 3. LLM parse the first listing page (price/stock/SKU) + pagination
                new PaginateStoreActivity(),        // 4. walk the next pages until none / the page cap
                new ClassifyStoreProductsActivity(),// 5. LLM relevance filter
                new StoreCatalogProductsActivity()  // 6. deep-dive top matches (ProductDetailWorkflow) + persist the rest
            }
        };
    }
}
