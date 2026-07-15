using Daleel.Core.Geo;
using Daleel.Core.Models;
using Daleel.Pipeline.Extraction;
using Daleel.Web.Events;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Daleel.Web.Pipeline.SubWorkflows;

// The four steps of the BRAND crawl. Catalogue navigation (not commerce): find where the product catalogue
// lives, walk its product lines/series extracting model specs, then deep-dive the top models through
// ProductDetailWorkflow and persist. Every render routes through the metered agent + is saved to R2; the
// deadline propagates via CancellationToken. Each step is best-effort.

/// <summary>Step 1 — render the brand homepage and LLM-locate its product catalogue + matching product lines.</summary>
[Activity("Daleel", "Crawl", "Find catalogue: LLM locates the product catalogue section (not marketing)")]
public sealed class FindCatalogSectionActivity : CancellableActivity
{
    protected override async ValueTask DoExecuteAsync(ActivityExecutionContext context)
    {
        var state = context.GetRequiredService<BrandCrawlState>();
        var services = context.GetRequiredService<SubWorkflowServices>();
        if (string.IsNullOrWhiteSpace(state.SiteUrl))
        {
            return;
        }

        services.Log($"🏭 Crawling brand {Display(state)} — locating the product catalogue…");
        var page = await CrawlPipeline.RenderAndSaveAsync(context, state.SiteUrl, state.SearchId, state.SiteUrl, context.CancellationToken);
        if (page is null)
        {
            state.RecordEvent(EventCategory.Extract, "crawl.brand.assess", "cf-browser", success: false,
                metadata: new Dictionary<string, object?> { ["site"] = state.SiteUrl, ["reason"] = "render-failed" });
            return;
        }

        state.Catalog = await services.Agent.AssessBrandCatalogAsync(
            state.SiteUrl, page.Content, state.Category, context.CancellationToken);
        services.Log($"🏭 {Display(state)}: catalogue {(state.Catalog.HasCatalog ? "found" : "not found")} " +
                     $"({state.Catalog.ProductLineUrls.Count} line(s)).");
        state.RecordEvent(EventCategory.Extract, "crawl.brand.assess", page.Provider,
            success: state.Catalog.HasCatalog,
            metadata: new Dictionary<string, object?>
            {
                ["site"] = state.SiteUrl,
                ["catalogUrl"] = state.Catalog.CatalogUrl,
                ["productLines"] = state.Catalog.ProductLineUrls.Count,
                ["platform"] = state.Catalog.Platform,
                ["notes"] = state.Catalog.Notes
            });
    }

    internal static string Display(BrandCrawlState s) => string.IsNullOrWhiteSpace(s.BrandName) ? s.SiteUrl : s.BrandName;
}

/// <summary>
/// Step 2 — walk the catalogue entry points (the catalogue landing + each matching product line) plus their
/// pagination, extracting product models from each. A single bounded queue handles both the multiple entry
/// points and the per-page "next" links, capped by <c>PipelineLimits.CrawlMaxPages</c> total pages.
/// </summary>
[Activity("Daleel", "Crawl", "Navigate product lines: LLM extracts model specs across the catalogue pages")]
public sealed class NavigateProductLinesActivity : CancellableActivity
{
    protected override async ValueTask DoExecuteAsync(ActivityExecutionContext context)
    {
        var state = context.GetRequiredService<BrandCrawlState>();
        var services = context.GetRequiredService<SubWorkflowServices>();
        var geo = GeoProfiles.ResolveOrDefault(state.Geo);
        var maxPages = PipelineLimits.CrawlMaxPages;

        if (!state.Catalog.HasCatalog)
        {
            return; // no catalogue located — nothing to walk
        }

        // One bounded queue over BOTH the entry points (catalogue landing + product lines) and their next-page
        // links, so a brand with several series and paginated lines is walked breadth-first within one budget.
        var queue = new Queue<string>(state.Catalog.EntryPoints);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (queue.Count > 0 && state.PagesFetched < maxPages && !context.CancellationToken.IsCancellationRequested)
        {
            var url = queue.Dequeue();
            if (!seen.Add(url))
            {
                continue;
            }

            var page = await CrawlPipeline.RenderAndSaveAsync(context, state.SiteUrl, state.SearchId, url, context.CancellationToken);
            if (page is null)
            {
                continue;
            }

            var result = await services.Agent.ExtractBrandModelsAsync(
                page.Content, url, state.Category, geo, context.CancellationToken);
            var added = CrawlPipeline.AddDiscovered(state.Discovered, result.Products);
            state.PagesFetched++;
            if (result.NextPageUrl is { } next)
            {
                queue.Enqueue(next);
            }

            services.Log($"🏭 {FindCatalogSectionActivity.Display(state)}: {added} model(s) from a catalogue page ({state.Discovered.Count} total).");
        }

        state.RecordEvent(EventCategory.Extract, "crawl.brand.navigate", "pipeline",
            metadata: new Dictionary<string, object?>
            {
                ["site"] = state.SiteUrl,
                ["pages"] = state.PagesFetched,
                ["models"] = state.Discovered.Count,
                ["stoppedAtCap"] = state.PagesFetched >= maxPages
            });
    }
}

/// <summary>Step 3 — LLM relevance-filter the discovered models against the wanted category.</summary>
[Activity("Daleel", "Crawl", "Classify brand models: drop models that aren't the wanted category")]
public sealed class ClassifyBrandModelsActivity : CancellableActivity
{
    protected override async ValueTask DoExecuteAsync(ActivityExecutionContext context)
    {
        var state = context.GetRequiredService<BrandCrawlState>();
        var services = context.GetRequiredService<SubWorkflowServices>();
        if (state.Discovered.Count == 0)
        {
            return;
        }

        var relevant = await services.Agent.ClassifyListingsAsync(state.Category, state.Discovered, context.CancellationToken);
        if (relevant.Count != state.Discovered.Count)
        {
            state.Discovered.Clear();
            state.Discovered.AddRange(relevant);
        }
    }
}

/// <summary>
/// Step 4 — deep-dive the top models through <see cref="ProductDetailWorkflow"/> (full specs + images), then
/// batch-persist the remaining models at listing level so none are lost. Bounded by <c>CrawlMaxDeepDive</c>.
/// </summary>
[Activity("Daleel", "Crawl", "Store brand models: deep-dive top models + persist the rest")]
public sealed class StoreBrandModelsActivity : CancellableActivity
{
    protected override async ValueTask DoExecuteAsync(ActivityExecutionContext context)
    {
        var state = context.GetRequiredService<BrandCrawlState>();
        var services = context.GetRequiredService<SubWorkflowServices>();
        var logger = context.GetRequiredService<ILogger<StoreBrandModelsActivity>>();
        var geo = GeoProfiles.ResolveOrDefault(state.Geo);
        if (state.Discovered.Count == 0)
        {
            return;
        }

        var deepDiveTargets = state.Discovered
            .Where(p => !string.IsNullOrWhiteSpace(p.Url) && !ListingExtractor.IsListingPageUrl(p.Url))
            .Take(PipelineLimits.CrawlMaxDeepDive)
            .ToList();

        var deepStates = await DeepDiveAsync(context, state, services, deepDiveTargets);

        // Only actually-persisted deep-dives are excluded from the remainder; a timed-out/faulted target
        // falls back to the listing-level persist so no discovered model is dropped.
        var persistedKeys = new HashSet<string>(
            deepStates.Where(s => s.Persisted).Select(s => KeyOf(s.Result)), StringComparer.OrdinalIgnoreCase);
        var deepDived = persistedKeys.Count;
        var remainder = state.Discovered.Where(p => !persistedKeys.Contains(KeyOf(p))).ToList();
        var (entities, prices) = await CrawlPersistence.PersistListingsAsync(
            context, remainder, geo, state.Category, state.BrandName, state.SearchId, logger, context.CancellationToken);

        state.Persisted = deepDived + entities;
        state.PricesRecorded = prices + deepStates.Count(s => s.Persisted && s.Result.Price is not null);
        services.Log($"🏭 {state.BrandName}: stored {state.Persisted} model(s) ({deepDived} deep-dived).");
        state.RecordEvent(EventCategory.Extract, "crawl.brand.store", "r2",
            metadata: new Dictionary<string, object?>
            {
                ["site"] = state.SiteUrl,
                ["discovered"] = state.Discovered.Count,
                ["deepDived"] = deepDived,
                ["persisted"] = state.Persisted
            });
    }

    private static async Task<IReadOnlyList<ProductDetailState>> DeepDiveAsync(
        ActivityExecutionContext context, BrandCrawlState state, SubWorkflowServices services,
        IReadOnlyList<ProductListing> targets)
    {
        if (targets.Count == 0)
        {
            return Array.Empty<ProductDetailState>();
        }

        services.Log($"🏭 {state.BrandName}: deep-diving {targets.Count} model page(s)…");
        var scopeFactory = context.GetRequiredService<IServiceScopeFactory>();
        return await SubWorkflowDispatcher.RunManyAsync<ProductDetailWorkflow, ProductDetailState, ProductListing>(
            scopeFactory, targets,
            (s, svc, product) =>
            {
                svc.Agent = services.Agent;
                svc.Progress = services.Progress;
                s.Geo = state.Geo;
                s.SearchId = state.SearchId;
                s.Listing = product;
                s.Result = product;
                s.Query = state.Category;
                s.SiteName = state.BrandName;
            },
            services.Progress, SubWorkflowDispatcher.DefaultTimeout, context.CancellationToken,
            maxConcurrency: PipelineLimits.CrawlConcurrency);
    }

    private static string KeyOf(ProductListing p) =>
        !string.IsNullOrWhiteSpace(p.Url)
            ? p.Url!.Trim().ToLowerInvariant()
            : string.Join('|', new[] { p.Brand, p.Model, p.Name }.Select(s => (s ?? string.Empty).Trim().ToLowerInvariant()));
}
