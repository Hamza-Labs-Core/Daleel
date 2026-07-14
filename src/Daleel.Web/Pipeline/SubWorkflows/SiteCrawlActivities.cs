using System.Text.Json;
using Daleel.Core.Geo;
using Daleel.Core.Models;
using Daleel.Pipeline.Extraction;
using Daleel.Search.Abstractions;
using Daleel.Web.Data;
using Daleel.Web.Events;
using Daleel.Web.Persistence;
using Daleel.Web.Profiles;
using Daleel.Web.Storage;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Microsoft.Extensions.Logging;

namespace Daleel.Web.Pipeline.SubWorkflows;

// The six steps of the LLM-driven per-site crawl. Every page is rendered through the METERED scraper
// (services.Agent.ReadPageAsync → Context.dev, then Cloudflare Browser Rendering) and saved to R2 (the
// save-everything rule); every LLM navigation decision is recorded to the event spine; every step threads
// context.CancellationToken so the 10-minute global deadline and the per-entity timeout unwind it. Each
// step is best-effort — a failed render/LLM call degrades to a safe default so the crawl can never fault
// the parent search.

/// <summary>Step 1 — render the site's landing page and ask the LLM what kind of site it is and how to
/// reach its product catalogue (platform, listing URLs, search URL, sitemap, product API endpoints).</summary>
[Activity("Daleel", "Crawl", "Assess the site: LLM reads the homepage to find the ways into the catalogue")]
public sealed class AssessSiteActivity : CancellableActivity
{
    protected override async ValueTask DoExecuteAsync(ActivityExecutionContext context)
    {
        var state = context.GetRequiredService<SiteCrawlState>();
        var services = context.GetRequiredService<SubWorkflowServices>();
        if (string.IsNullOrWhiteSpace(state.SiteUrl))
        {
            return; // nothing to crawl
        }

        services.Log($"🕷️ Crawling {DisplayName(state)} — reading the homepage…");
        var page = await CrawlPipeline.RenderAndSaveAsync(context, state, state.SiteUrl, context.CancellationToken);
        if (page is null)
        {
            state.RecordEvent(EventCategory.Extract, "crawl.assess", "cf-browser", success: false,
                metadata: new Dictionary<string, object?> { ["site"] = state.SiteUrl, ["reason"] = "render-failed" });
            return;
        }

        state.Assessment = await services.Agent.AssessSiteAsync(
            state.SiteUrl, page.Content, state.Query, context.CancellationToken);

        // Log the LLM's decision to the timeline so /admin/timeline shows how the crawler read the site.
        services.Log($"🕷️ {DisplayName(state)}: {state.Assessment.Kind} on {state.Assessment.Platform ?? "custom"} — {state.Assessment.RecommendedApproach}");
        state.RecordEvent(EventCategory.Extract, "crawl.assess", page.Provider,
            success: state.Assessment.HasEntryPoint,
            metadata: new Dictionary<string, object?>
            {
                ["site"] = state.SiteUrl,
                ["kind"] = state.Assessment.Kind.ToString(),
                ["platform"] = state.Assessment.Platform,
                ["approach"] = state.Assessment.RecommendedApproach.ToString(),
                ["listingUrls"] = state.Assessment.ListingUrls.Count,
                ["hasSearch"] = state.Assessment.SearchUrlTemplate is not null,
                ["apiEndpoints"] = state.Assessment.ApiEndpoints.Count,
                ["notes"] = state.Assessment.Notes
            });
    }

    private static string DisplayName(SiteCrawlState state) =>
        string.IsNullOrWhiteSpace(state.SiteName) ? state.SiteUrl : state.SiteName;
}

/// <summary>Step 2 — turn the LLM's recommended approach into the concrete first listing URL: use the
/// site search with the query, a category page, a product API endpoint, or the sitemap.</summary>
[Activity("Daleel", "Crawl", "Find listings: pick the best entry point from the LLM's assessment")]
public sealed class FindListingsActivity : CancellableActivity
{
    protected override ValueTask DoExecuteAsync(ActivityExecutionContext context)
    {
        var state = context.GetRequiredService<SiteCrawlState>();
        var services = context.GetRequiredService<SubWorkflowServices>();

        state.ListingUrl = CrawlNavigation.ResolveEntryPoint(state.Assessment, state.Query);
        if (state.ListingUrl is null)
        {
            services.Log($"🕷️ {state.SiteUrl}: no reachable catalogue entry point found.");
            state.RecordEvent(EventCategory.Extract, "crawl.findlistings", "pipeline", success: false,
                metadata: new Dictionary<string, object?> { ["site"] = state.SiteUrl });
            return ValueTask.CompletedTask;
        }

        state.RecordEvent(EventCategory.Extract, "crawl.findlistings", "pipeline",
            metadata: new Dictionary<string, object?>
            {
                ["site"] = state.SiteUrl,
                ["approach"] = state.Assessment.RecommendedApproach.ToString(),
                ["listingUrl"] = state.ListingUrl
            });
        return ValueTask.CompletedTask;
    }
}

/// <summary>Step 3 — render the first listing page and let the LLM extract its product cards + the
/// pagination signals (next-page URL / load-more / total pages).</summary>
[Activity("Daleel", "Crawl", "Extract listing: LLM parses the first listing page for products + pagination")]
public sealed class ExtractListingActivity : CancellableActivity
{
    protected override async ValueTask DoExecuteAsync(ActivityExecutionContext context)
    {
        var state = context.GetRequiredService<SiteCrawlState>();
        var services = context.GetRequiredService<SubWorkflowServices>();
        if (state.ListingUrl is null)
        {
            return;
        }

        var geo = GeoProfiles.ResolveOrDefault(state.Geo);
        var page = await CrawlPipeline.RenderAndSaveAsync(context, state, state.ListingUrl, context.CancellationToken);
        if (page is null)
        {
            return;
        }

        var result = await services.Agent.ExtractListingAsync(
            page.Content, state.ListingUrl, state.Query, geo, context.CancellationToken);
        var added = CrawlPipeline.AddDiscovered(state, result.Products);
        state.PagesFetched = 1;
        state.PaginationCursor = result.NextPageUrl;

        services.Log($"🕷️ {DisplayName(state)}: page 1 → {added} product(s).");
        state.RecordEvent(EventCategory.Extract, "crawl.extract", page.Provider,
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

    private static string DisplayName(SiteCrawlState state) =>
        string.IsNullOrWhiteSpace(state.SiteName) ? state.SiteUrl : state.SiteName;
}

/// <summary>Step 4 — walk the remaining listing pages: while the LLM reports a next page (and the page cap
/// isn't hit), render it, extract more products, and follow the pagination.</summary>
[Activity("Daleel", "Crawl", "Paginate: follow the LLM's next-page links up to the page cap")]
public sealed class PaginateActivity : CancellableActivity
{
    protected override async ValueTask DoExecuteAsync(ActivityExecutionContext context)
    {
        var state = context.GetRequiredService<SiteCrawlState>();
        var services = context.GetRequiredService<SubWorkflowServices>();
        var geo = GeoProfiles.ResolveOrDefault(state.Geo);
        var maxPages = PipelineLimits.CrawlMaxPages;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { state.ListingUrl ?? string.Empty };

        while (state.PaginationCursor is { } next &&
               state.PagesFetched < maxPages &&
               !context.CancellationToken.IsCancellationRequested)
        {
            // Guard against a site that points "next" back at a page we already walked — a cheap loop breaker
            // on top of the page cap.
            if (!seen.Add(next))
            {
                break;
            }

            var page = await CrawlPipeline.RenderAndSaveAsync(context, state, next, context.CancellationToken);
            if (page is null)
            {
                break; // can't render the next page — stop rather than spin
            }

            var result = await services.Agent.ExtractListingAsync(
                page.Content, next, state.Query, geo, context.CancellationToken);
            var added = CrawlPipeline.AddDiscovered(state, result.Products);
            state.PagesFetched++;
            state.PaginationCursor = result.NextPageUrl;
            services.Log($"🕷️ {state.SiteName}: page {state.PagesFetched} → +{added} ({state.Discovered.Count} total).");
        }

        state.RecordEvent(EventCategory.Extract, "crawl.paginate", "pipeline",
            metadata: new Dictionary<string, object?>
            {
                ["site"] = state.SiteUrl,
                ["pages"] = state.PagesFetched,
                ["products"] = state.Discovered.Count,
                ["stoppedAtCap"] = state.PagesFetched >= maxPages
            });
    }
}

/// <summary>Step 5 — deep-dive each discovered product (that has a detail URL) in bounded parallel: render
/// its detail page and let the LLM extract the full record (brand, SKU, images, price, specs, seller).</summary>
[Activity("Daleel", "Crawl", "Deep-dive products: LLM extracts each product's detail page (fan-out)")]
public sealed class DeepDiveProductsActivity : CancellableActivity
{
    protected override async ValueTask DoExecuteAsync(ActivityExecutionContext context)
    {
        var state = context.GetRequiredService<SiteCrawlState>();
        var services = context.GetRequiredService<SubWorkflowServices>();
        var geo = GeoProfiles.ResolveOrDefault(state.Geo);

        // Only products with a real detail URL are worth a visit; cap the count so the crawl stays bounded
        // (each deep-dive is a render + an LLM call) — the rest keep their listing-level data.
        var targets = state.Discovered
            .Select((listing, index) => (listing, index))
            .Where(t => IsDeepDiveable(t.listing.Url))
            .Take(PipelineLimits.CrawlMaxDeepDive)
            .ToList();
        if (targets.Count == 0)
        {
            return;
        }

        services.Log($"🕷️ {state.SiteName}: deep-diving {targets.Count} product page(s)…");
        using var gate = new SemaphoreSlim(Math.Max(1, PipelineLimits.CrawlConcurrency));
        var enriched = 0;

        var tasks = targets.Select(async t =>
        {
            await gate.WaitAsync(context.CancellationToken).ConfigureAwait(false);
            try
            {
                var page = await CrawlPipeline.RenderAndSaveAsync(context, state, t.listing.Url!, context.CancellationToken);
                if (page is null)
                {
                    return;
                }

                var full = await services.Agent.DeepDiveProductAsync(
                    page.Content, t.listing, geo, context.CancellationToken);
                lock (state.Discovered)
                {
                    state.Discovered[t.index] = full;
                }
                Interlocked.Increment(ref enriched);
            }
            catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
            {
                throw; // the deadline / a real cancel must stop the crawl
            }
            catch
            {
                // best-effort per product — a bad detail page keeps the listing-level record.
            }
            finally
            {
                gate.Release();
            }
        });

        // A genuine cancel/deadline (the per-task handler rethrew it) must propagate to stop the crawl — do
        // NOT swallow it here. A non-cancellation per-product failure was already absorbed inside the task,
        // so Task.WhenAll only surfaces cancellation, which the workflow's outer machinery handles.
        await Task.WhenAll(tasks).ConfigureAwait(false);

        state.RecordEvent(EventCategory.Extract, "crawl.deepdive", "cf-browser",
            metadata: new Dictionary<string, object?>
            {
                ["site"] = state.SiteUrl,
                ["attempted"] = targets.Count,
                ["enriched"] = enriched
            });
    }

    /// <summary>A product is worth deep-diving when it links to its own detail page (not a listing/search URL).</summary>
    private static bool IsDeepDiveable(string? url) =>
        !string.IsNullOrWhiteSpace(url) && !ListingExtractor.IsListingPageUrl(url);
}

/// <summary>Step 6 — LLM-classify the discovered products against the query (drop the irrelevant ones),
/// then persist the survivors: rich JSON to R2 as EntityDocuments (source of truth), the Postgres index
/// row, and each priced product to the ScrapedPrice series.</summary>
[Activity("Daleel", "Crawl", "Classify & store: relevance filter → R2 EntityDocuments + index + prices")]
public sealed class ClassifyAndStoreActivity : CancellableActivity
{
    protected override async ValueTask DoExecuteAsync(ActivityExecutionContext context)
    {
        var state = context.GetRequiredService<SiteCrawlState>();
        var services = context.GetRequiredService<SubWorkflowServices>();
        var logger = context.GetRequiredService<ILogger<ClassifyAndStoreActivity>>();
        if (state.Discovered.Count == 0)
        {
            return;
        }

        // 1. Relevance classify — drop accessories/unrelated items the crawl swept up.
        var relevant = await services.Agent.ClassifyListingsAsync(
            state.Query, state.Discovered, context.CancellationToken);
        if (relevant.Count == 0)
        {
            return;
        }

        // 2. Aggregate listings → distinct models (dedupes the same product across pages) and project to
        //    self-contained EntityDocuments, then persist to R2 (truth) + the Postgres index in one call.
        var geo = GeoProfiles.ResolveOrDefault(state.Geo);
        var models = ListingAggregator.Aggregate(relevant);
        var now = Now(context);
        var result = new ProductSearchResult
        {
            Query = state.Query,
            Geo = geo.Key,
            Country = geo.Country,
            Models = models
        };
        var docs = EntityDocumentMapper.ToDocuments(result, SearchIntentType.Product, state.SearchId, now);
        state.Persisted = await SafePersistEntities(context, docs, logger);

        // 3. Persist each priced product to the ScrapedPrice series so the store page + per-product price
        //    comparison read the crawl's prices back (the same durable record the old single-page fetch wrote).
        state.PricesRecorded = await PersistPricesAsync(context, state, relevant, now, logger);

        services.Log($"🕷️ {state.SiteName}: stored {state.Persisted} product(s) ({state.PricesRecorded} priced).");
        state.RecordEvent(EventCategory.Extract, "crawl.store", "r2",
            metadata: new Dictionary<string, object?>
            {
                ["site"] = state.SiteUrl,
                ["discovered"] = state.Discovered.Count,
                ["relevant"] = relevant.Count,
                ["models"] = models.Count,
                ["persisted"] = state.Persisted,
                ["priced"] = state.PricesRecorded
            });
    }

    private static async Task<int> SafePersistEntities(
        ActivityExecutionContext context, IReadOnlyList<Daleel.Core.Persistence.EntityDocument> docs, ILogger logger)
    {
        if (docs.Count == 0 || context.GetService<ISearchEntityStore>() is not { } store)
        {
            return 0;
        }

        try
        {
            return await store.SaveAllAsync(docs, context.CancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // best-effort: persisting the crawl must never fail the search, but the failure must be visible.
            logger.LogWarning(ex, "Failed to persist {Count} crawled entity documents", docs.Count);
            return 0;
        }
    }

    private static async Task<int> PersistPricesAsync(
        ActivityExecutionContext context, SiteCrawlState state,
        IReadOnlyList<ProductListing> listings, DateTimeOffset now, ILogger logger)
    {
        var storeName = string.IsNullOrWhiteSpace(state.SiteName) ? state.SiteUrl : state.SiteName;
        var rows = listings
            .Where(l => l.Price is not null && !string.IsNullOrWhiteSpace(l.Name))
            .Select(l => new ScrapedPrice
            {
                ProductName = l.Name,
                ProductKey = ProductProfile.KeyFor(l.Brand, l.Model, l.Name),
                StoreName = storeName,
                Price = l.Price,
                Currency = l.Currency,
                SourceUrl = l.Url,
                Provider = "site-crawl",
                ScrapedAt = now
            })
            .ToList();
        if (rows.Count == 0 || context.GetService<IScrapedPriceRepository>() is not { } repo)
        {
            return 0;
        }

        try
        {
            await repo.AddRangeAsync(rows, context.CancellationToken);
            return rows.Count;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to persist {Count} crawled prices for {Store}", rows.Count, storeName);
            return 0;
        }
    }

    private static DateTimeOffset Now(ActivityExecutionContext context) =>
        context.GetService<ProfileOptions>()?.Now() ?? DateTimeOffset.UtcNow;
}
