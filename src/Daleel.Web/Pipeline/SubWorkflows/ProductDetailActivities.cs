using Daleel.Core.Geo;
using Daleel.Web.Events;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Microsoft.Extensions.Logging;

namespace Daleel.Web.Pipeline.SubWorkflows;

// The two steps of the per-product detail workflow. Extraction renders the product page and asks the LLM
// for the full record; persistence writes the rich EntityDocument (all images + specs + reviews) that the
// listing-level path would flatten. Best-effort throughout; the deadline propagates via CancellationToken.

/// <summary>Step 1 — render the product detail page and LLM-extract its full record onto the result.</summary>
[Activity("Daleel", "Crawl", "Extract product detail: LLM mines the full record from the product page")]
public sealed class ExtractProductDetailActivity : CancellableActivity
{
    protected override async ValueTask DoExecuteAsync(ActivityExecutionContext context)
    {
        var state = context.GetRequiredService<ProductDetailState>();
        var services = context.GetRequiredService<SubWorkflowServices>();

        // Result starts as the listing-level record so a render/extract miss still flows the product through.
        state.Result = state.Listing;
        var url = state.Listing.Url;
        if (string.IsNullOrWhiteSpace(url))
        {
            return; // no detail page to visit
        }

        var geo = GeoProfiles.ResolveOrDefault(state.Geo);
        var page = await CrawlPipeline.RenderAndSaveAsync(context, url, state.SearchId, url, context.CancellationToken);
        if (page is null)
        {
            return;
        }

        var (enriched, detail) = await services.Agent.ExtractProductDetailAsync(
            page.Content, state.Listing, geo, context.CancellationToken);
        state.Result = enriched;
        state.Detail = detail;

        state.RecordEvent(EventCategory.Extract, "crawl.detail", page.Provider,
            success: detail is not null,
            metadata: new Dictionary<string, object?>
            {
                ["product"] = state.Result.Name,
                ["url"] = url,
                ["images"] = detail?.Images.Count ?? 0,
                ["specs"] = detail?.Specs.Count ?? 0,
                ["reviews"] = detail?.Reviews.Count ?? 0
            });
    }
}

/// <summary>Step 2 — persist the enriched product as a rich EntityDocument (all images + specs) + price.</summary>
[Activity("Daleel", "Crawl", "Store product detail: rich EntityDocument + index + price observation")]
public sealed class StoreProductDetailActivity : CancellableActivity
{
    protected override async ValueTask DoExecuteAsync(ActivityExecutionContext context)
    {
        var state = context.GetRequiredService<ProductDetailState>();
        var logger = context.GetRequiredService<ILogger<StoreProductDetailActivity>>();
        var geo = GeoProfiles.ResolveOrDefault(state.Geo);

        state.Persisted = await CrawlPersistence.PersistDetailAsync(
            context, state.Result, state.Detail, geo, state.Query, state.SiteName, state.SearchId, logger,
            context.CancellationToken);
    }
}
