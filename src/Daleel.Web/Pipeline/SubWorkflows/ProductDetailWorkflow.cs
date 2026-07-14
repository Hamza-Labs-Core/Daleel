using Elsa.Workflows;
using Elsa.Workflows.Activities;

namespace Daleel.Web.Pipeline.SubWorkflows;

/// <summary>
/// The LLM extractor for a single product DETAIL page — the third specialised crawler. Given one product's
/// detail URL it renders the page (CF Browser) and mines the full record: every image, the complete spec
/// sheet, price, description, features, buyer reviews, related products, and the seller — then persists it as
/// a rich R2 <c>EntityDocument</c> + Postgres index row + price observation.
/// </summary>
/// <remarks>
/// Dispatched as a fan-out target (one per discovered product) by <see cref="StoreCrawlWorkflow"/> and
/// <see cref="BrandCrawlWorkflow"/>, each in its own DI scope, and usable standalone for a single product URL.
/// A flat two-step <see cref="Sequence"/>: extraction is deliberately separate from persistence so a render/
/// LLM failure still leaves the durable store untouched. Both steps route through the metered agent and thread
/// the deadline via <c>context.CancellationToken</c>.
/// </remarks>
public sealed class ProductDetailWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Root = new Sequence
        {
            Activities =
            {
                new ExtractProductDetailActivity(),  // 1. render the detail page → LLM full record
                new StoreProductDetailActivity()      // 2. persist the rich EntityDocument + index + price
            }
        };
    }
}
