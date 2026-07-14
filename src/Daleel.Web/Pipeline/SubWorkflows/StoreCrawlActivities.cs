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

// The six steps of the STORE crawl. Commerce navigation: assess the platform, reach the catalogue via
// search/category/API, walk the paginated priced listings, then deep-dive the top matches through
// ProductDetailWorkflow and persist. Every render routes through the metered agent + is saved to R2; every
// LLM decision is recorded; the deadline propagates via CancellationToken. Each step is best-effort.

/// <summary>Step 1 — render the store homepage and LLM-assess its platform + ways into the catalogue.</summary>
[Activity("Daleel", "Crawl", "Assess store: LLM reads the homepage for platform + catalogue entry points")]
public sealed class AssessStoreActivity : CancellableActivity
{
    protected override async ValueTask DoExecuteAsync(ActivityExecutionContext context)
    {
        var state = context.GetRequiredService<StoreCrawlState>();
        var services = context.GetRequiredService<SubWorkflowServices>();
        if (string.IsNullOrWhiteSpace(state.SiteUrl))
        {
            return;
        }

        services.Log($"🛒 Crawling store {Display(state)} — reading the homepage…");
        var page = await CrawlPipeline.RenderAndSaveAsync(context, state.SiteUrl, state.SearchId, state.SiteUrl, context.CancellationToken);
        if (page is null)
        {
            state.RecordEvent(EventCategory.Extract, "crawl.store.assess", "cf-browser", success: false,
                metadata: new Dictionary<string, object?> { ["site"] = state.SiteUrl, ["reason"] = "render-failed" });
            return;
        }

        state.Assessment = await services.Agent.AssessStoreAsync(
            state.SiteUrl, page.Content, state.Query, context.CancellationToken);
        services.Log($"🛒 {Display(state)}: {state.Assessment.Platform ?? "custom"} store — {state.Assessment.RecommendedApproach}");
        state.RecordEvent(EventCategory.Extract, "crawl.store.assess", page.Provider,
            success: state.Assessment.HasEntryPoint,
            metadata: new Dictionary<string, object?>
            {
                ["site"] = state.SiteUrl,
                ["platform"] = state.Assessment.Platform,
                ["approach"] = state.Assessment.RecommendedApproach.ToString(),
                ["listingUrls"] = state.Assessment.ListingUrls.Count,
                ["hasSearch"] = state.Assessment.SearchUrlTemplate is not null,
                ["apiEndpoints"] = state.Assessment.ApiEndpoints.Count,
                ["notes"] = state.Assessment.Notes
            });
    }

    internal static string Display(StoreCrawlState s) => string.IsNullOrWhiteSpace(s.SiteName) ? s.SiteUrl : s.SiteName;
}

/// <summary>Step 2 — resolve the LLM's recommended approach to the concrete first listing URL.</summary>
[Activity("Daleel", "Crawl", "Find store listings: pick the entry point (search/category/api/sitemap)")]
public sealed class FindStoreListingsActivity : CancellableActivity
{
    protected override ValueTask DoExecuteAsync(ActivityExecutionContext context)
    {
        var state = context.GetRequiredService<StoreCrawlState>();
        var services = context.GetRequiredService<SubWorkflowServices>();

        state.ListingUrl = CrawlNavigation.ResolveEntryPoint(state.Assessment, state.Query);
        if (state.ListingUrl is null)
        {
            services.Log($"🛒 {state.SiteUrl}: no reachable catalogue entry point.");
            state.RecordEvent(EventCategory.Extract, "crawl.store.find", "pipeline", success: false,
                metadata: new Dictionary<string, object?> { ["site"] = state.SiteUrl });
            return ValueTask.CompletedTask;
        }

        state.RecordEvent(EventCategory.Extract, "crawl.store.find", "pipeline",
            metadata: new Dictionary<string, object?>
            {
                ["site"] = state.SiteUrl,
                ["approach"] = state.Assessment.RecommendedApproach.ToString(),
                ["listingUrl"] = state.ListingUrl
            });
        return ValueTask.CompletedTask;
    }
}

/// <summary>Step 3 — render the first listing page and LLM-extract priced product cards + pagination.</summary>
[Activity("Daleel", "Crawl", "Extract store listing: LLM parses priced product cards + pagination")]
public sealed class ExtractStoreListingActivity : CancellableActivity
{
    protected override async ValueTask DoExecuteAsync(ActivityExecutionContext context)
    {
        var state = context.GetRequiredService<StoreCrawlState>();
        var services = context.GetRequiredService<SubWorkflowServices>();
        if (state.ListingUrl is null)
        {
            return;
        }

        var geo = GeoProfiles.ResolveOrDefault(state.Geo);
        var page = await CrawlPipeline.RenderAndSaveAsync(context, state.SiteUrl, state.SearchId, state.ListingUrl, context.CancellationToken);
        if (page is null)
        {
            return;
        }

        var result = await services.Agent.ExtractStoreListingAsync(
            page.Content, state.ListingUrl, state.Query, geo, context.CancellationToken);
        var added = CrawlPipeline.AddDiscovered(state.Discovered, result.Products);
        state.PagesFetched = 1;
        state.PaginationCursor = result.NextPageUrl;

        services.Log($"🛒 {AssessStoreActivity.Display(state)}: page 1 → {added} product(s).");
        state.RecordEvent(EventCategory.Extract, "crawl.store.extract", page.Provider,
            metadata: new Dictionary<string, object?>
            {
                ["site"] = state.SiteUrl,
                ["url"] = state.ListingUrl,
                ["found"] = result.Products.Count,
                ["added"] = added,
                ["hasNext"] = result.NextPageUrl is not null,
                ["totalPages"] = result.TotalPages
            });
    }
}

/// <summary>Step 4 — walk the remaining listing pages until none or the page cap.</summary>
[Activity("Daleel", "Crawl", "Paginate store: follow next-page links up to the page cap")]
public sealed class PaginateStoreActivity : CancellableActivity
{
    protected override async ValueTask DoExecuteAsync(ActivityExecutionContext context)
    {
        var state = context.GetRequiredService<StoreCrawlState>();
        var services = context.GetRequiredService<SubWorkflowServices>();
        var geo = GeoProfiles.ResolveOrDefault(state.Geo);
        var maxPages = PipelineLimits.CrawlMaxPages;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { state.ListingUrl ?? string.Empty };

        while (state.PaginationCursor is { } next &&
               state.PagesFetched < maxPages &&
               !context.CancellationToken.IsCancellationRequested)
        {
            if (!seen.Add(next))
            {
                break; // next points back at a walked page — stop rather than loop
            }

            var page = await CrawlPipeline.RenderAndSaveAsync(context, state.SiteUrl, state.SearchId, next, context.CancellationToken);
            if (page is null)
            {
                break;
            }

            var result = await services.Agent.ExtractStoreListingAsync(
                page.Content, next, state.Query, geo, context.CancellationToken);
            var added = CrawlPipeline.AddDiscovered(state.Discovered, result.Products);
            state.PagesFetched++;
            state.PaginationCursor = result.NextPageUrl;
            services.Log($"🛒 {state.SiteName}: page {state.PagesFetched} → +{added} ({state.Discovered.Count} total).");
        }

        state.RecordEvent(EventCategory.Extract, "crawl.store.paginate", "pipeline",
            metadata: new Dictionary<string, object?>
            {
                ["site"] = state.SiteUrl,
                ["pages"] = state.PagesFetched,
                ["products"] = state.Discovered.Count,
                ["stoppedAtCap"] = state.PagesFetched >= maxPages
            });
    }
}

/// <summary>Step 5 — LLM relevance-filter the discovered products against the query.</summary>
[Activity("Daleel", "Crawl", "Classify store products: drop items that aren't the queried product")]
public sealed class ClassifyStoreProductsActivity : CancellableActivity
{
    protected override async ValueTask DoExecuteAsync(ActivityExecutionContext context)
    {
        var state = context.GetRequiredService<StoreCrawlState>();
        var services = context.GetRequiredService<SubWorkflowServices>();
        if (state.Discovered.Count == 0)
        {
            return;
        }

        var relevant = await services.Agent.ClassifyListingsAsync(state.Query, state.Discovered, context.CancellationToken);
        if (relevant.Count != state.Discovered.Count)
        {
            state.Discovered.Clear();
            state.Discovered.AddRange(relevant);
        }
    }
}

/// <summary>
/// Step 6 — deep-dive the top matches through <see cref="ProductDetailWorkflow"/> (each renders its detail
/// page, extracts the full record, and persists a rich EntityDocument), then batch-persist the remaining
/// discovered products at listing level so none are lost. Bounded by <c>CrawlMaxDeepDive</c>.
/// </summary>
[Activity("Daleel", "Crawl", "Store catalogue: deep-dive top matches + persist the rest")]
public sealed class StoreCatalogProductsActivity : CancellableActivity
{
    protected override async ValueTask DoExecuteAsync(ActivityExecutionContext context)
    {
        var state = context.GetRequiredService<StoreCrawlState>();
        var services = context.GetRequiredService<SubWorkflowServices>();
        var logger = context.GetRequiredService<ILogger<StoreCatalogProductsActivity>>();
        var geo = GeoProfiles.ResolveOrDefault(state.Geo);
        if (state.Discovered.Count == 0)
        {
            return;
        }

        // Products with a real detail URL are worth a full deep-dive (bounded); the rest keep listing data.
        var deepDiveTargets = state.Discovered
            .Where(p => !string.IsNullOrWhiteSpace(p.Url) && !ListingExtractor.IsListingPageUrl(p.Url))
            .Take(PipelineLimits.CrawlMaxDeepDive)
            .ToList();

        var deepStates = await DeepDiveAsync(context, state, services, deepDiveTargets);

        // Only products the deep-dive ACTUALLY persisted are removed from the remainder — a target whose
        // ProductDetailWorkflow timed out/faulted (RunChildAsync returns it un-persisted) falls back to the
        // listing-level persist below, so no discovered product is silently dropped.
        var persistedKeys = new HashSet<string>(
            deepStates.Where(s => s.Persisted).Select(s => KeyOf(s.Result)), StringComparer.OrdinalIgnoreCase);
        var deepDived = persistedKeys.Count;
        var remainder = state.Discovered.Where(p => !persistedKeys.Contains(KeyOf(p))).ToList();
        var (entities, prices) = await CrawlPersistence.PersistListingsAsync(
            context, remainder, geo, state.Query, state.SiteName, state.SearchId, logger, context.CancellationToken);

        state.Persisted = deepDived + entities;
        // Count only prices that were actually written: the remainder's (PersistListingsAsync) plus the
        // successfully deep-dived priced products. Counting un-persisted targets would falsely mark the crawl
        // as "yielded" and wrongly skip the store's single-page fallback.
        state.PricesRecorded = prices + deepStates.Count(s => s.Persisted && s.Result.Price is not null);
        services.Log($"🛒 {state.SiteName}: stored {state.Persisted} product(s) ({deepDived} deep-dived).");
        state.RecordEvent(EventCategory.Extract, "crawl.store.store", "r2",
            metadata: new Dictionary<string, object?>
            {
                ["site"] = state.SiteUrl,
                ["discovered"] = state.Discovered.Count,
                ["deepDived"] = deepDived,
                ["persisted"] = state.Persisted,
                ["priced"] = state.PricesRecorded
            });
    }

    /// <summary>Fans out one <see cref="ProductDetailWorkflow"/> per target (throttled by <c>CrawlConcurrency</c>); returns the finished child states.</summary>
    private static async Task<IReadOnlyList<ProductDetailState>> DeepDiveAsync(
        ActivityExecutionContext context, StoreCrawlState state, SubWorkflowServices services,
        IReadOnlyList<ProductListing> targets)
    {
        if (targets.Count == 0)
        {
            return Array.Empty<ProductDetailState>();
        }

        services.Log($"🛒 {state.SiteName}: deep-diving {targets.Count} product page(s)…");
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
                s.Query = state.Query;
                s.SiteName = state.SiteName;
            },
            services.Progress, SubWorkflowDispatcher.DefaultTimeout, context.CancellationToken,
            maxConcurrency: PipelineLimits.CrawlConcurrency);
    }

    private static string KeyOf(ProductListing p) =>
        !string.IsNullOrWhiteSpace(p.Url)
            ? p.Url!.Trim().ToLowerInvariant()
            : string.Join('|', new[] { p.Brand, p.Model, p.Name }.Select(s => (s ?? string.Empty).Trim().ToLowerInvariant()));
}
