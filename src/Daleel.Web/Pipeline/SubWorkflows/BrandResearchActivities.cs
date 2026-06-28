using Daleel.Core.Models;
using Daleel.Web.Data;
using Daleel.Web.Events;
using Daleel.Web.Profiles;
using Daleel.Web.Storage;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;

namespace Daleel.Web.Pipeline.SubWorkflows;

// The five steps of the per-brand research sub-workflow. They orchestrate the lower-level
// IBrandRepository + IProfileResearcher directly (the DB-first/staleness orchestration that used to
// live inside BrandProfileService now lives here, in the workflow), and each step no-ops gracefully
// when its prerequisite is missing so a brand with no profile simply flows through un-enriched.

/// <summary>Step 1 — find the brand's saved profile (its local site + reputation), DB-first.</summary>
[Activity("Daleel", "Brand", "Search the brand's local site: serve the saved profile when fresh")]
public sealed class SearchBrandSiteActivity : CodeActivity
{
    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
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
public sealed class ScrapeBrandCatalogActivity : CodeActivity
{
    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
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
        state.Researched = await SafeResearch(researcher, state.Brand.Name, state.Geo, context.CancellationToken);
        state.RecordEvent(EventCategory.Profile, "profile.brand", "context.dev",
            success: state.Researched is not null,
            metadata: new Dictionary<string, object?>
            {
                ["name"] = state.Brand.Name,
                ["found"] = state.Researched is not null
            });
    }

    private static async Task<Data.Brand?> SafeResearch(
        IProfileResearcher researcher, string name, string geo, CancellationToken ct)
    {
        try { return await researcher.ResearchBrandAsync(name, geo, ct); }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }
}

/// <summary>Step 3 — fold the synthesized 0–10 profile onto the UI's 1–5 reputation shape.</summary>
[Activity("Daleel", "Brand", "Synthesize: map the researched profile onto the brand's reputation")]
public sealed class SynthesizeBrandProfileActivity : CodeActivity
{
    protected override ValueTask ExecuteAsync(ActivityExecutionContext context)
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
public sealed class SaveBrandProfileActivity : CodeActivity
{
    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
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
/// Step 5 — download the brand logo and store it in the Cloudflare R2 <see cref="R2Bucket.Images"/> bucket,
/// then point the result at the stable hosted copy so the brand card stops hot-linking the source. The store
/// is best-effort: it degrades to the original URL when R2 is unconfigured, the images bucket has no public
/// host, or the fetch/upload fails — the sub-workflow never fails over a logo.
/// </summary>
[Activity("Daleel", "Brand", "Download the brand logo to the R2 images bucket when configured")]
public sealed class DownloadBrandImagesActivity : CodeActivity
{
    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var state = context.GetRequiredService<BrandResearchState>();
        var services = context.GetRequiredService<SubWorkflowServices>();

        var logoUrl = state.Result.LogoUrl;
        if (string.IsNullOrWhiteSpace(logoUrl))
        {
            return; // no brand logo located — nothing to store
        }

        var r2 = context.GetRequiredService<IR2StorageService>();
        if (!r2.IsConfigured)
        {
            RecordImages(state, success: false, reason: "r2-not-configured");
            services.Report(SearchStep.BuildingProfiles, "Progress.Msg.LocatedLogoNoStorage", state.Brand.Name);
            return;
        }

        // Mirror how product shots are keyed (brands/{id}/…) so a brand's logo and product images share a
        // folder and re-running enrichment overwrites in place rather than piling up duplicates.
        var stored = await SafeStoreImage(r2, logoUrl, $"brands/{state.Result.Id}", context.CancellationToken);

        // StoreImageAsync hands back the ORIGINAL url unchanged when it could not host the image (no images
        // public host, an SSRF-blocked target, or a fetch/upload failure). A *changed* url is the signal that
        // a real hosted copy now exists — only then do we rewrite the result and claim success.
        var hosted = !string.IsNullOrWhiteSpace(stored)
                     && !string.Equals(stored, logoUrl, StringComparison.OrdinalIgnoreCase);
        if (hosted)
        {
            state.Result = state.Result with { LogoUrl = stored };
        }

        RecordImages(state, success: hosted, reason: hosted ? null : "images-host-unset-or-fetch-failed");
        services.Report(SearchStep.BuildingProfiles,
            hosted ? "Progress.Msg.StoredLogo" : "Progress.Msg.LocatedLogoNoHost", state.Brand.Name);
    }

    private static void RecordImages(BrandResearchState state, bool success, string? reason) =>
        state.RecordEvent(EventCategory.Profile, "brand.images", "r2",
            success: success,
            metadata: new Dictionary<string, object?>
            {
                ["name"] = state.Brand.Name,
                ["images"] = 1,
                ["stored"] = success,
                ["reason"] = reason
            });

    private static async Task<string?> SafeStoreImage(
        IR2StorageService r2, string url, string prefix, CancellationToken ct)
    {
        // StoreImageAsync is already best-effort (never throws), but guard anyway so a logo store can never
        // fail the brand sub-workflow.
        try { return await r2.StoreImageAsync(url, prefix, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch { return url; }
    }
}
