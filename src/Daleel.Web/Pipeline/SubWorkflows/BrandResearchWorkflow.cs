using Elsa.Workflows;
using Elsa.Workflows.Activities;

namespace Daleel.Web.Pipeline.SubWorkflows;

/// <summary>
/// Per-brand research sub-workflow, dispatched in parallel (one child per brand) from the main
/// <see cref="SearchWorkflow"/>. It resolves the brand's local site, scrapes its catalogue + synthesizes
/// a reputation profile via Context.dev, persists it, and locates its images — operating on a scoped
/// <see cref="BrandResearchState"/> the dispatcher seeds and reads back.
/// </summary>
public sealed class BrandResearchWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Root = new Sequence
        {
            Activities =
            {
                new SearchBrandSiteActivity(),       // 1. find the local site (DB-first)
                new ScrapeBrandCatalogActivity(),    // 2. scrape models/specs/images via Context.dev
                new SynthesizeBrandProfileActivity(),// 3. LLM reputation profile → UI shape
                new SaveBrandProfileActivity(),      // 4. persist for reuse
                new DownloadBrandImagesActivity(),   // 5. images → object storage (when configured)
                new CrawlBrandSiteActivity()         // 6. LLM-crawl the brand site (additive product discovery)
            }
        };
    }
}
