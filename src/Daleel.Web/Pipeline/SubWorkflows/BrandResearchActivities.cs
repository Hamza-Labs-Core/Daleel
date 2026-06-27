using Daleel.Core.Models;
using Daleel.Web.Data;
using Daleel.Web.Events;
using Daleel.Web.Profiles;
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

        services.Log($"Finding {state.Brand.Name}'s local site…");
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

        services.Log($"Scraping {state.Brand.Name}'s catalogue via Context.dev…");
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
        services.Log($"Built reputation profile for {b.Name}.");
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
            services.Log($"Saved {state.Brand.Name}'s profile.");
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
/// Step 5 — locate the brand's images (logo + product shots) for storage. An object-store (Cloudflare
/// R2) image sink isn't wired yet — R2 is configured here only for error-log shipping — so this records
/// the located images and logs that storage is pending rather than silently doing nothing.
/// </summary>
[Activity("Daleel", "Brand", "Download brand images to object storage (R2) when configured")]
public sealed class DownloadBrandImagesActivity : CodeActivity
{
    protected override ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var state = context.GetRequiredService<BrandResearchState>();
        var services = context.GetRequiredService<SubWorkflowServices>();

        var images = new[] { state.Result.LogoUrl }
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Select(u => u!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (images.Count == 0)
        {
            return ValueTask.CompletedTask;
        }

        // No R2 image bucket is wired, so we don't claim to have stored anything — we record the located
        // images so the seam is observable and a future R2 sink can pick them up.
        state.RecordEvent(EventCategory.Profile, "brand.images", "r2",
            success: false,
            metadata: new Dictionary<string, object?>
            {
                ["name"] = state.Brand.Name,
                ["images"] = images.Count,
                ["stored"] = false
            });
        services.Log($"Located {images.Count} image(s) for {state.Brand.Name} (object storage not configured).");
        return ValueTask.CompletedTask;
    }
}
