using Daleel.Search.Providers;
using Daleel.Web.Data;
using Daleel.Web.Events;
using Daleel.Web.Profiles;
using Daleel.Web.Services;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Microsoft.Extensions.Logging;

namespace Daleel.Web.Pipeline.SubWorkflows;

// The five steps of the per-store research sub-workflow. Steps 1–4 reuse the IStoreRepository +
// IProfileResearcher pieces (the researcher scrapes the site, verifies on Google Maps and extracts
// contact info in one Context.dev + Places pass); each step folds the slice it owns onto the result.
// Step 5 harvests live catalogue prices — the one genuinely new per-store network call.

/// <summary>Step 1 — scrape the store's site for listings/contact, DB-first.</summary>
[Activity("Daleel", "Store", "Scrape the store site: serve the saved profile when fresh, else research")]
public sealed class ScrapeStoreSiteActivity : CancellableActivity
{
    protected override async ValueTask DoExecuteAsync(ActivityExecutionContext context)
    {
        var state = context.GetRequiredService<StoreResearchState>();
        var services = context.GetRequiredService<SubWorkflowServices>();
        var repo = context.GetRequiredService<IStoreRepository>();
        var options = context.GetRequiredService<ProfileOptions>();

        services.Report(SearchStep.FindingStores, "Progress.Msg.LookingUpStore", state.Store.Name);
        state.Existing = await SafeGet(repo, state.Store.Name, context.CancellationToken);
        if (state.Existing is not null && !state.Existing.IsStale(options.Now(), options.Ttl))
        {
            state.ResolvedFromCache = true;
            state.Saved = state.Existing;
            return;
        }

        var researcher = context.GetRequiredService<IProfileResearcher>();
        if (!researcher.IsAvailable)
        {
            state.Saved = state.Existing; // degrade to the (possibly stale) saved profile
            return;
        }

        services.Report(SearchStep.FindingStores, "Progress.Msg.ScrapingStoreSite", state.Store.Name);
        state.Researched = await SafeResearch(researcher, state.Store.Name, state.Geo, context.CancellationToken);
        state.Saved = state.Researched ?? state.Existing;
        state.RecordEvent(EventCategory.Profile, "profile.store", "context.dev",
            success: state.Researched is not null,
            metadata: new Dictionary<string, object?>
            {
                ["name"] = state.Store.Name,
                ["found"] = state.Researched is not null
            });
    }

    private static async Task<Data.Store?> SafeGet(IStoreRepository repo, string name, CancellationToken ct)
    {
        try { return await repo.GetByNameAsync(name, ct); }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private static async Task<Data.Store?> SafeResearch(
        IProfileResearcher researcher, string name, string geo, CancellationToken ct)
    {
        try { return await researcher.ResearchStoreAsync(name, geo, ct); }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }
}

/// <summary>Step 2 — cross-reference Google Maps for verified location, rating and review count.</summary>
[Activity("Daleel", "Store", "Verify on Google Maps: fold in verified rating, reviews and coordinates")]
public sealed class VerifyOnMapsActivity : CancellableActivity
{
    protected override ValueTask DoExecuteAsync(ActivityExecutionContext context)
    {
        var state = context.GetRequiredService<StoreResearchState>();
        var services = context.GetRequiredService<SubWorkflowServices>();
        if (state.Saved is not { } saved)
        {
            return ValueTask.CompletedTask;
        }

        services.Report(SearchStep.FindingStores, "Progress.Msg.VerifyingStoreMaps", state.Store.Name);
        var s = state.Result;
        state.Result = s with
        {
            // Prefer the live result's own fields; backfill from the Maps-verified profile.
            Rating = s.Rating ?? saved.GoogleRating ?? saved.Rating,
            ReviewCount = s.ReviewCount ?? saved.GoogleReviewCount,
            Latitude = s.Latitude ?? saved.Latitude,
            Longitude = s.Longitude ?? saved.Longitude
        };
        state.RecordEvent(EventCategory.Places, "store.verify", "places",
            success: saved.IsVerified,
            metadata: new Dictionary<string, object?>
            {
                ["name"] = state.Store.Name,
                ["verified"] = saved.IsVerified
            });
        return ValueTask.CompletedTask;
    }
}

/// <summary>Step 3 — fold the store's contact details (phone, address) onto the result.</summary>
[Activity("Daleel", "Store", "Extract contact info: phone and address from the verified profile")]
public sealed class ExtractContactInfoActivity : CancellableActivity
{
    protected override ValueTask DoExecuteAsync(ActivityExecutionContext context)
    {
        var state = context.GetRequiredService<StoreResearchState>();
        if (state.Saved is not { } saved)
        {
            return ValueTask.CompletedTask;
        }

        var s = state.Result;
        state.Result = s with
        {
            Address = s.Address ?? saved.Address ?? saved.Location,
            Phone = s.Phone ?? saved.Phone
        };
        return ValueTask.CompletedTask;
    }
}

/// <summary>Step 4 — persist the verified store profile and finalize its website link.</summary>
[Activity("Daleel", "Store", "Save the verified store profile to the database")]
public sealed class SaveStoreProfileActivity : CancellableActivity
{
    protected override async ValueTask DoExecuteAsync(ActivityExecutionContext context)
    {
        var state = context.GetRequiredService<StoreResearchState>();
        var services = context.GetRequiredService<SubWorkflowServices>();
        if (state.Saved is { } saved)
        {
            var s = state.Result;
            state.Result = s with
            {
                Url = s.Url ?? saved.Website ?? saved.GoogleMapsUrl,
                // Carry the real database id (from a cache hit) so the store page routes by it; the
                // freshly-researched path backfills it below once the profile is persisted.
                DbId = saved.Id > 0 ? saved.Id : s.DbId
            };
        }

        if (state.Researched is null)
        {
            return; // served from cache (or nothing found) — nothing new to persist
        }

        var repo = context.GetRequiredService<IStoreRepository>();
        var options = context.GetRequiredService<ProfileOptions>();
        state.Researched.LastRefreshed = options.Now();
        var persisted = await SafeUpsert(repo, state.Researched, context.CancellationToken);
        if (persisted is not null)
        {
            if (persisted.Id > 0)
            {
                state.Result = state.Result with { DbId = persisted.Id };
            }
            services.Report(SearchStep.FindingStores, "Progress.Msg.SavedStoreProfile", state.Store.Name);
        }
    }

    private static async Task<Data.Store?> SafeUpsert(IStoreRepository repo, Data.Store store, CancellationToken ct)
    {
        try { return await repo.UpsertAsync(store, ct); }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }
}

/// <summary>
/// Step 5 — harvest the store's product catalogue WITH PRICING from its website
/// (Context.dev <c>/v1/brand/ai/products</c>) and persist each priced product to the
/// <see cref="ScrapedPrice"/> time series so the store page and per-product price comparison can read it
/// back. Best-effort and bounded by the sub-workflow timeout: any failure just means "no live prices for
/// this store". Records how many priced products were found.
/// </summary>
/// <remarks>
/// Two execution paths (docs/architecture/cloudflare-workers-pipeline.md, Phase 1a). When the Cloudflare
/// execution layer is configured AND the <c>cloudflare.execution.enabled</c> flag is on, the crawl is
/// SUBMITTED to the edge scrape-worker and this activity returns immediately — deep, uncapped crawls no
/// longer race the sub-workflow timeout, and the poll-drain service persists the result whenever it
/// lands (even after the search finishes or faults). Otherwise the original inline Context.dev call
/// runs unchanged as the fallback.
/// </remarks>
[Activity("Daleel", "Store", "Scrape prices: harvest the store's catalogue prices via Context.dev")]
public sealed class ScrapePricesActivity : CancellableActivity
{

    protected override async ValueTask DoExecuteAsync(ActivityExecutionContext context)
    {
        var state = context.GetRequiredService<StoreResearchState>();
        var services = context.GetRequiredService<SubWorkflowServices>();
        // ALL provider calls go through the gateway (metered by construction) — never a direct
        // provider construction here. The unmetered inline crawl this activity used to make was
        // exactly the leak the gateway exists to close.
        var api = context.GetRequiredService<Daleel.Web.Services.IProviderApi>();
        var logger = context.GetRequiredService<ILogger<ScrapePricesActivity>>();

        var domain = DomainOf(state.Result.Url);
        if (domain is null)
        {
            return; // no resolvable store domain — nothing to crawl
        }

        // Edge path: fire the submit and move on. The result is durable in R2 the moment the worker
        // finishes and reaches the ScrapedPrice series via the drain — nothing here can lose it.
        if (await TrySubmitToEdgeAsync(context, state, services, api, domain, logger))
        {
            return;
        }

        if (!api.HasScraper)
        {
            return; // no Context.dev key — nothing to crawl inline
        }

        services.Report(SearchStep.FindingStores, "Progress.Msg.ReadingStoreCatalog", state.Store.Name);
        var products = await SafeCatalog(api, domain, logger, state.Store.Name, context.CancellationToken);

        // Persist every priced product as a new observation so the data isn't discarded after the crawl.
        var persisted = await PersistPricesAsync(context, state, products, logger);
        state.PricedProducts = persisted;
        state.RecordEvent(EventCategory.Extract, "store.prices", "context.dev",
            metadata: new Dictionary<string, object?>
            {
                ["store"] = state.Store.Name,
                ["domain"] = domain,
                ["products"] = products.Count,
                ["priced"] = state.PricedProducts
            });
        if (state.PricedProducts > 0)
        {
            services.Report(SearchStep.FindingStores, "Progress.Msg.FoundPricedProducts",
                state.PricedProducts, state.Store.Name);
        }
    }

    /// <summary>
    /// Submits the catalogue crawl to the edge scrape-worker when the execution layer is configured,
    /// enabled, and reachable. Returns true when the job was handed off (the drain service owns
    /// persistence from here); false falls back to the inline path. Best-effort by design — any failure
    /// here must degrade to inline, never fault the store sub-workflow.
    /// </summary>
    private static async Task<bool> TrySubmitToEdgeAsync(
        ActivityExecutionContext context, StoreResearchState state, SubWorkflowServices services,
        Daleel.Web.Services.IProviderApi api, string domain, ILogger logger)
    {
        if (!api.HasEdge)
        {
            return false; // CF_SCRAPE_WORKER_URL/TOKEN not configured
        }

        // Submitting replaces the INLINE persistence, so the whole return path must exist before we
        // hand off: the poll queue (drain) credentials AND R2 (where the result lands). A partial
        // configuration would strand every crawl result — silent, permanent price loss.
        if (!api.EdgeDrainReady)
        {
            logger.LogWarning(
                "Cloudflare execution requested but the drain path is incomplete — staying inline so results aren't stranded");
            return false;
        }

        try
        {
            var config = context.GetRequiredService<ISystemConfigService>();
            if (!await config.GetBoolAsync(
                    Daleel.Web.Cloudflare.CloudflareWorkerOptions.EnabledFlag, false, context.CancellationToken))
            {
                return false; // admin flag off — inline path stays authoritative
            }

            // 0 ⇒ uncapped: the vendor's own ceiling applies. The whole point of the edge path is that
            // a deep catalogue (a 184-product collection page) no longer races a synchronous timeout.
            var maxProducts = await config.GetIntAsync(
                Daleel.Web.Cloudflare.CloudflareWorkerOptions.CatalogMaxProductsKey, 0, context.CancellationToken);

            // Through the gateway: the submit is metered with the same estimate an inline crawl
            // records, so edge spend counts toward this job's cap and usage log at submit time.
            var handle = await api.SubmitEdgeCatalogAsync(
                domain, state.Store.Name, state.SearchId, maxProducts, context.CancellationToken);
            if (handle is null)
            {
                return false; // worker unreachable/rejecting — degrade to inline
            }

            services.Report(SearchStep.FindingStores, "Progress.Msg.ReadingStoreCatalog", state.Store.Name);
            state.RecordEvent(EventCategory.Extract, "store.prices.submitted", "scrape-worker",
                metadata: new Dictionary<string, object?>
                {
                    ["store"] = state.Store.Name,
                    ["domain"] = domain,
                    ["jobId"] = handle.JobId,
                    ["resultKey"] = handle.ResultKey,
                    ["maxProducts"] = maxProducts
                });
            logger.LogInformation(
                "Catalogue crawl for {Store} ({Domain}) submitted to scrape-worker as job {JobId}",
                state.Store.Name, domain, handle.JobId);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Edge catalogue submit failed for {Store} ({Domain}); falling back to inline",
                state.Store.Name, domain);
            return false;
        }
    }

    /// <summary>
    /// Writes one <see cref="ScrapedPrice"/> row per priced catalogue product and returns how many were
    /// saved. Best-effort: a DB failure is logged and treated as "no prices stored" so it never fails the
    /// store sub-workflow.
    /// </summary>
    private static async Task<int> PersistPricesAsync(
        ActivityExecutionContext context, StoreResearchState state,
        IReadOnlyList<CatalogProduct> products, ILogger logger)
    {
        var now = context.GetRequiredService<ProfileOptions>().Now();
        var rows = products
            .Where(p => p.Price is not null && !string.IsNullOrWhiteSpace(p.Name))
            .Select(p => new ScrapedPrice
            {
                ProductName = p.Name,
                // Same normalized brand+model key ProductProfile/ScrapedPrice share, so a per-product page
                // can collapse observations across stores into one comparison.
                ProductKey = ProductProfile.KeyFor(null, null, p.Name),
                StoreName = state.Store.Name,
                Price = p.Price,
                Currency = p.Currency,
                SourceUrl = p.Url,
                Provider = "context.dev",
                ScrapedAt = now
            })
            .ToList();
        if (rows.Count == 0)
        {
            return 0;
        }

        var repo = context.GetRequiredService<IScrapedPriceRepository>();
        try
        {
            await repo.AddRangeAsync(rows, context.CancellationToken);
            return rows.Count;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to persist {Count} scraped prices for store {Store}", rows.Count, state.Store.Name);
            return 0;
        }
    }

    private static async Task<IReadOnlyList<CatalogProduct>> SafeCatalog(
        Daleel.Web.Services.IProviderApi api, string domain, ILogger logger, string storeName, CancellationToken ct)
    {
        try
        {
            // UNCAPPED (maxProducts 0 ⇒ vendor ceiling): the whole point is the full catalogue, not
            // its first page. Time-bounded by the sub-workflow budget; cost-bounded by the job cap.
            return await api.ExtractCatalogAsync(domain, maxProducts: 0, ct: ct);
        }
        catch (OperationCanceledException) { throw; } // genuine cancellation/timeout must propagate
        catch (Exception ex)
        {
            // Any other failure → no live prices for this store, but make the failure visible.
            logger.LogWarning(ex,
                "Catalogue price scrape failed for store {Store} (domain {Domain})", storeName, domain);
            return Array.Empty<CatalogProduct>();
        }
    }

    /// <summary>Bare registrable domain of a store URL (drops scheme + leading www), or null.</summary>
    private static string? DomainOf(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var s = url.Trim();
        if (!s.Contains("://"))
        {
            s = "https://" + s;
        }

        if (!Uri.TryCreate(s, UriKind.Absolute, out var u))
        {
            return null;
        }

        return u.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? u.Host[4..] : u.Host;
    }
}
