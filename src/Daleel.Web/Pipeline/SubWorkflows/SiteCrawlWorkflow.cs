using Elsa.Workflows;
using Elsa.Workflows.Activities;

namespace Daleel.Web.Pipeline.SubWorkflows;

/// <summary>
/// The LLM-driven site-crawl sub-workflow — an intelligent replacement for the old "fetch one page and
/// extract" catalogue harvest. Given a site URL and the shopper's query, it uses the LLM at every step to
/// navigate the site and pull out every matching product: it assesses the homepage to find the ways into
/// the catalogue, picks the best entry point, extracts each listing page (products + pagination), walks the
/// pages, deep-dives each product's detail page in parallel, then classifies and persists the results.
/// </summary>
/// <remarks>
/// Dispatched per-site (one per discovered store/brand) via <see cref="SubWorkflowDispatcher"/>, operating
/// on a scoped <see cref="SiteCrawlState"/>. Every page is rendered through the METERED scraper
/// (<c>AgentService.ReadPageAsync</c> → Context.dev, then Cloudflare Browser Rendering) — never a direct
/// provider — so the whole crawl is cost-accounted and SSRF-guarded. Each fetched page is saved to R2
/// (the save-everything rule) and every LLM navigation decision is logged to the event spine. Bounded by
/// <c>PipelineLimits.CrawlMaxPages</c>/<c>CrawlMaxDeepDive</c>, the per-entity sub-workflow timeout, and the
/// 10-minute global deadline (each activity threads <c>context.CancellationToken</c>).
///
/// Follows the same DELIBERATE flat-<see cref="Sequence"/> convention as <see cref="SearchWorkflow"/>: the
/// loop (pagination) and fan-out (deep-dive) live in plain C# inside the activities, not as Elsa
/// <c>ForEach</c>/parallel nodes — Elsa is the registration + sequencing + per-step telemetry seam here.
/// </remarks>
public sealed class SiteCrawlWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Root = new Sequence
        {
            Activities =
            {
                new AssessSiteActivity(),           // 1. render homepage → LLM: type/platform/entry points
                new FindListingsActivity(),         // 2. pick the best entry point (search/category/api/sitemap)
                new ExtractListingActivity(),       // 3. LLM parse the first listing page → products + pagination
                new PaginateActivity(),             // 4. walk the next pages until none / the page cap
                new DeepDiveProductsActivity(),     // 5. fan out: LLM-extract each product's detail page
                new ClassifyAndStoreActivity()      // 6. LLM relevance filter → persist to R2 + index + prices
            }
        };
    }
}
