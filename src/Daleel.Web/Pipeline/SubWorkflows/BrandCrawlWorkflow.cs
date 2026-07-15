using Elsa.Workflows;
using Elsa.Workflows.Activities;

namespace Daleel.Web.Pipeline.SubWorkflows;

/// <summary>
/// The LLM crawler specialised for BRAND / manufacturer sites (lg.com, sharp.com …). Its navigation pattern is
/// fundamentally different from a store's: instead of a price-bearing search, it first finds the product
/// CATALOGUE section (explicitly not the marketing homepage), then walks the product lines/series under it
/// extracting model specs/images/features (brand sites rarely quote prices), deep-dives the top models via
/// <see cref="ProductDetailWorkflow"/>, and persists them as product entities.
/// </summary>
/// <remarks>
/// Dispatched per-brand from brand research (additive — the brand catalogue scrape still feeds the reputation/
/// image steps) via <see cref="CrawlDispatch"/>, on a scoped <see cref="BrandCrawlState"/>. Every page renders
/// through the METERED scraper, is archived to R2, and every LLM decision is logged. Bounded by
/// <c>PipelineLimits.CrawlMaxPages</c>/<c>CrawlMaxDeepDive</c>, the per-entity timeout, and the 10-minute
/// deadline. Flat <see cref="Sequence"/> per the pipeline convention.
/// </remarks>
public sealed class BrandCrawlWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Root = new Sequence
        {
            Activities =
            {
                new FindCatalogSectionActivity(),   // 1. render homepage → LLM: locate the catalogue + product lines
                new NavigateProductLinesActivity(), // 2. walk the catalogue/line pages extracting model specs (+ pagination)
                new ClassifyBrandModelsActivity(),  // 3. LLM relevance filter
                new StoreBrandModelsActivity()      // 4. deep-dive top models (ProductDetailWorkflow) + persist the rest
            }
        };
    }
}
