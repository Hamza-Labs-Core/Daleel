using Daleel.Core.Models;
using Daleel.Web.Data;
using Daleel.Web.Events;
using Daleel.Web.Profiles;
using Daleel.Web.Storage;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Microsoft.Extensions.Logging;

namespace Daleel.Web.Pipeline.SubWorkflows;

// The five steps of the per-brand research sub-workflow. They orchestrate the lower-level
// IBrandRepository + IProfileResearcher directly (the DB-first/staleness orchestration that used to
// live inside BrandProfileService now lives here, in the workflow), and each step no-ops gracefully
// when its prerequisite is missing so a brand with no profile simply flows through un-enriched.

/// <summary>Step 1 — find the brand's saved profile (its local site + reputation), DB-first.</summary>
[Activity("Daleel", "Brand", "Search the brand's local site: serve the saved profile when fresh")]
public sealed class SearchBrandSiteActivity : CancellableActivity
{
    protected override async ValueTask DoExecuteAsync(ActivityExecutionContext context)
    {
        var state = context.GetRequiredService<BrandResearchState>();
        var services = context.GetRequiredService<SubWorkflowServices>();
        var repo = context.GetRequiredService<IBrandRepository>();
        var options = context.GetRequiredService<ProfileOptions>();

        services.Report(SearchStep.BuildingProfiles, "Progress.Msg.FindingBrandSite", state.Brand.Name);
        state.Existing = await SafeGet(repo, state.Brand.Name, context.CancellationToken);
        if (state.Existing is not null && !state.Existing.IsStale(options.Now(), options.Ttl))
        {
            // Fresh saved profile — reuse it and skip the (slow, paid) Context.dev research.
            state.ResolvedFromCache = true;
            state.Saved = state.Existing;
        }
    }

    private static async Task<Data.Brand?> SafeGet(IBrandRepository repo, string name, CancellationToken ct)
    {
        try { return await repo.GetByNameAsync(name, ct); }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }
}

/// <summary>Step 2 — scrape the brand's catalogue (models, specs, images) via Context.dev.</summary>
[Activity("Daleel", "Brand", "Scrape the brand catalogue + synthesize a reputation profile via Context.dev")]
public sealed class ScrapeBrandCatalogActivity : CancellableActivity
{
    protected override async ValueTask DoExecuteAsync(ActivityExecutionContext context)
    {
        var state = context.GetRequiredService<BrandResearchState>();
        var services = context.GetRequiredService<SubWorkflowServices>();
        if (state.ResolvedFromCache)
        {
            return; // the fresh saved profile already has the catalogue-derived reputation
        }

        var researcher = context.GetRequiredService<IProfileResearcher>();
        if (!researcher.IsAvailable)
        {
            return; // no Context.dev/LLM keys — degrade to the (possibly stale) saved profile
        }

        services.Report(SearchStep.BuildingProfiles, "Progress.Msg.ScrapingBrandCatalog", state.Brand.Name);
        // The saved (stale) profile's website is a real URL a previous pass verified — hand it to
        // the researcher so it skips re-discovery; with no hint the researcher discovers the site.
        state.Researched = await SafeResearch(
            researcher, state.Brand.Name, state.Geo, state.Existing?.Website, context.CancellationToken);
        state.RecordEvent(EventCategory.Profile, "profile.brand", "context.dev",
            success: state.Researched is not null,
            metadata: new Dictionary<string, object?>
            {
                ["name"] = state.Brand.Name,
                ["found"] = state.Researched is not null
            });
    }

    private static async Task<Data.Brand?> SafeResearch(
        IProfileResearcher researcher, string name, string geo, string? siteHint, CancellationToken ct)
    {
        try { return await researcher.ResearchBrandAsync(name, geo, ct, siteHint); }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }
}

/// <summary>Step 3 — fold the synthesized 0–10 profile onto the UI's 1–5 reputation shape.</summary>
[Activity("Daleel", "Brand", "Synthesize: map the researched profile onto the brand's reputation")]
public sealed class SynthesizeBrandProfileActivity : CancellableActivity
{
    protected override ValueTask DoExecuteAsync(ActivityExecutionContext context)
    {
        var state = context.GetRequiredService<BrandResearchState>();
        var services = context.GetRequiredService<SubWorkflowServices>();
        var saved = state.Researched ?? state.Existing;
        if (saved is null)
        {
            return ValueTask.CompletedTask; // nothing to add — Result stays the bare extracted brand
        }

        state.Saved = saved;
        var b = state.Brand;
        state.Result = b with
        {
            Reputation = b.Reputation ?? ToReputation(saved),
            Url = b.Url ?? saved.Website,
            // Carry the real database id so the brand page routes by it (a freshly-researched profile
            // has no id until SaveBrandProfileActivity persists it — that step backfills it then).
            DbId = saved.Id > 0 ? saved.Id : b.DbId
        };
        services.Report(SearchStep.BuildingProfiles, "Progress.Msg.BuiltReputation", b.Name);
        return ValueTask.CompletedTask;
    }

    /// <summary>Maps a saved 0–10 brand profile onto the UI's 1–5 <see cref="BrandReputation"/> shape.</summary>
    internal static BrandReputation ToReputation(Data.Brand saved) => new()
    {
        Brand = saved.Name,
        Score = saved.ReputationScore is { } r ? Math.Clamp(r / 2.0, 0, 5) : null,
        Pros = saved.Pros,
        Complaints = saved.Cons,
        Summary = saved.Description
    };
}

/// <summary>Step 4 — persist a freshly-researched profile so the next search reuses it.</summary>
[Activity("Daleel", "Brand", "Save the brand profile to the database")]
public sealed class SaveBrandProfileActivity : CancellableActivity
{
    protected override async ValueTask DoExecuteAsync(ActivityExecutionContext context)
    {
        var state = context.GetRequiredService<BrandResearchState>();
        var services = context.GetRequiredService<SubWorkflowServices>();
        if (state.Researched is null)
        {
            return; // served from cache (or nothing found) — nothing new to persist
        }

        var repo = context.GetRequiredService<IBrandRepository>();
        var options = context.GetRequiredService<ProfileOptions>();
        state.Researched.LastRefreshed = options.Now();
        var saved = await SafeUpsert(repo, state.Researched, context.CancellationToken);
        if (saved is not null)
        {
            state.Saved = saved;
            // Backfill the now-persisted database id onto the result so the brand page routes by it.
            if (saved.Id > 0)
            {
                state.Result = state.Result with { DbId = saved.Id };
            }
            services.Report(SearchStep.BuildingProfiles, "Progress.Msg.SavedBrandProfile", state.Brand.Name);
        }
    }

    private static async Task<Data.Brand?> SafeUpsert(IBrandRepository repo, Data.Brand brand, CancellationToken ct)
    {
        try { return await repo.UpsertAsync(brand, ct); }
        catch (OperationCanceledException) { throw; }
        catch { return null; } // best-effort: a failed save must never fail the search
    }
}

/// <summary>
/// Step 5 — keep the brand logo as its original source URL so the brand card renders it directly. We
/// deliberately do NOT copy the logo into Cloudflare R2: the "hosted" URL was only valid when the images
/// bucket's public host was bound to the exact bucket we wrote to, and any mismatch silently 404'd every
/// stored image. Hot-linking the source (external https logos are CSP-allowed) is the policy across the app —
/// images are source URLs, never re-hosted — and removes that whole failure mode.
/// </summary>
[Activity("Daleel", "Brand", "Locate the brand logo and keep its source URL (no R2 upload)")]
public sealed class DownloadBrandImagesActivity : CancellableActivity
{
    protected override ValueTask DoExecuteAsync(ActivityExecutionContext context)
    {
        var state = context.GetRequiredService<BrandResearchState>();
        var services = context.GetRequiredService<SubWorkflowServices>();

        var logoUrl = state.Result.LogoUrl;
        if (string.IsNullOrWhiteSpace(logoUrl))
        {
            return ValueTask.CompletedTask; // no brand logo located — nothing to record
        }

        // The logo URL is already on the result from profile research; we keep it verbatim and render it
        // directly. Record that a logo was located (sourced, not R2-hosted) for the admin event stream.
        RecordImages(state, located: true);
        services.Report(SearchStep.BuildingProfiles, "Progress.Msg.LocatedLogoNoStorage", state.Brand.Name);
        return ValueTask.CompletedTask;
    }

    private static void RecordImages(BrandResearchState state, bool located) =>
        state.RecordEvent(EventCategory.Profile, "brand.images", "source",
            success: located,
            metadata: new Dictionary<string, object?>
            {
                ["name"] = state.Brand.Name,
                ["images"] = located ? 1 : 0,
                ["stored"] = false,
                ["reason"] = "source-url-direct"
            });
}

/// <summary>
/// New terminal step — additionally crawl the brand's own website with the LLM-driven
/// <see cref="SiteCrawlWorkflow"/> to discover + persist its catalogue products. ADDITIVE (unlike the store
/// crawl, which replaces the single-page fetch): the brand's <see cref="ScrapeBrandCatalogActivity"/> output
/// still feeds the reputation-synthesis + image steps upstream, so this doesn't replace it — it enriches the
/// durable product store (R2 EntityDocuments + index + prices) with what the intelligent crawl finds.
/// Best-effort and gated on Cloudflare Browser Rendering + the crawl flag, so it no-ops when the renderer
/// isn't configured — leaving the brand pipeline exactly as it was.
/// </summary>
[Activity("Daleel", "Brand", "Crawl the brand site with the LLM navigator (additive product discovery)")]
public sealed class CrawlBrandSiteActivity : CancellableActivity
{
    protected override async ValueTask DoExecuteAsync(ActivityExecutionContext context)
    {
        var state = context.GetRequiredService<BrandResearchState>();
        var logger = context.GetRequiredService<ILogger<CrawlBrandSiteActivity>>();

        try
        {
            var crawl = await CrawlDispatch.TryRunAsync(
                context, state.Result.Url, state.Brand.Name, state.Query,
                SiteKind.Brand, state, context.CancellationToken);
            if (crawl is null)
            {
                return; // crawl unavailable — the brand pipeline already did its work
            }

            state.RecordEvent(EventCategory.Extract, "brand.crawl", "site-crawl",
                metadata: new Dictionary<string, object?>
                {
                    ["brand"] = state.Brand.Name,
                    ["site"] = state.Result.Url,
                    ["persisted"] = crawl.Persisted,
                    ["priced"] = crawl.PricesRecorded,
                    ["pages"] = crawl.PagesFetched
                });
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Additive + best-effort: a crawl failure must never fault brand research.
            logger.LogWarning(ex, "LLM site crawl failed for brand {Brand} ({Site})", state.Brand.Name, state.Result.Url);
        }
    }
}
