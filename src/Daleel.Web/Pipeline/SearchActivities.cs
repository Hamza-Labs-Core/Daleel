using System.Text.Json;
using Daleel.Agent;
using Daleel.Core.Geo;
using Daleel.Core.Models;
using Daleel.Web.Events;
using Daleel.Web.Profiles;
using Daleel.Web.Services;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;

namespace Daleel.Web.Pipeline;

// The nine steps of the search pipeline, each a discrete Elsa CodeActivity. They share the scoped
// SearchPipelineState (resolved from the activity's DI context) and delegate the heavy lifting to the
// existing, well-tested AgentService stages — so the workflow owns orchestration without
// reimplementing plan/gather/analyze/project. Every post-cache activity no-ops on a cache hit.

/// <summary>Step 1 — normalize the query: resolve the market and run the LLM planner into a strategy.</summary>
[Activity("Daleel", "Search", "Plan: resolve market + expand the query into a bilingual search strategy")]
public sealed class ParseQueryActivity : CodeActivity
{
    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var state = context.GetRequiredService<SearchPipelineState>();
        state.Log("Analyzing your request…");
        // The query itself is the strongest market signal ("AC in Dubai" → UAE), so it overrides the
        // stored/auto-detected default. Fall back to the request's geo when the query names no market.
        state.GeoProfile = GeoProfiles.DetectInText(state.Query) ?? GeoProfiles.ResolveOrDefault(state.Geo);
        state.Geo = state.GeoProfile.Key;
        state.Log($"Focusing on the {state.GeoProfile.Country} market…");
        state.Strategy = await state.Agent.PlanAsync(
            PromptTemplates.PlanFreeform(state.Query, state.GeoProfile), context.CancellationToken);

        var subject = state.Strategy.Subject is { Length: > 0 } s ? s : state.Query;
        state.Log($"Looking for {Humanize(state.Strategy.QueryType)}: {subject}");
    }

    /// <summary>Turns the QueryType enum into a friendly noun for the status line.</summary>
    private static string Humanize(QueryType type) => type switch
    {
        QueryType.ProductResearch => "products",
        QueryType.BrandLookup => "brand info",
        QueryType.StoreFinder => "stores",
        QueryType.DealHunter => "deals",
        QueryType.OpinionAggregation => "opinions",
        QueryType.Comparison => "a comparison",
        _ => "answers"
    };
}

/// <summary>Step 2 — replay a stored report for an identical recent search, short-circuiting the rest.</summary>
[Activity("Daleel", "Search", "Check the result cache and short-circuit on a hit")]
public sealed class CheckCacheActivity : CodeActivity
{
    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var state = context.GetRequiredService<SearchPipelineState>();
        if (state.Cache is null)
        {
            return;
        }

        state.Log("Checking our answers vault…");
        try
        {
            var payload = await state.Cache.GetAsync(state.ResultKey, context.CancellationToken);
            if (payload is null)
            {
                state.RecordEvent(EventCategory.Cache, "cache.miss", "cache");
                return;
            }

            var cached = JsonSerializer.Deserialize<CachedSearchResult>(payload);
            if (cached is not null)
            {
                state.FromCache = true;
                state.ResultJson = cached.ResultJson;
                state.ResultType = cached.ResultType;
                state.FilteredCount = cached.FilteredCount;
                state.FilteredCategories = cached.FilteredCategories ?? string.Empty;
                state.RecordEvent(EventCategory.Cache, "cache.hit", "cache");
                state.Log("⚡ Loaded from cache — identical search run recently.");
            }
        }
        catch (OperationCanceledException)
        {
            throw; // a cap-trip / user cancel must stop the job, not be swallowed as a cache miss
        }
        catch
        {
            // Cache hiccup or corrupt payload ⇒ treat as a miss and run the search live.
        }
    }
}

/// <summary>Step 3 — fan out to every configured provider in parallel (web/shopping/places/social/scrape).</summary>
[Activity("Daleel", "Search", "Gather sources: run the strategy across all providers in parallel")]
public sealed class GatherSourcesActivity : CodeActivity
{
    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var state = context.GetRequiredService<SearchPipelineState>();
        if (state.FromCache || state.Strategy is null || state.GeoProfile is null)
        {
            return;
        }

        state.Log("Expanding the search across providers…");
        state.Bundle = await state.Agent.GatherAsync(state.Strategy, state.GeoProfile, context.CancellationToken);

        var b = state.Bundle;
        state.Log(
            $"Gathered {b.WebResults.Count} web, {b.ShoppingResults.Count} shopping and {b.Stores.Count} store sources.");
    }
}

/// <summary>Step 4 — the LLM structured pass: analyst summary plus product extraction for product queries.</summary>
[Activity("Daleel", "Search", "Extract products: LLM analyst summary + structured product projection")]
public sealed class ExtractProductsActivity : CodeActivity
{
    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var state = context.GetRequiredService<SearchPipelineState>();
        if (state.FromCache || state.Bundle is null || state.GeoProfile is null)
        {
            return;
        }

        state.Log($"Found {state.Bundle.Sources.Count} sources — reading and extracting…");
        var system = state.IsProductQuery ? PromptTemplates.ProductAnalystSystem : null;
        state.Summary = await state.Agent.AnalyzeAsync(
            state.Query, state.GeoProfile, state.Bundle, context.CancellationToken, system);

        if (state.IsProductQuery)
        {
            state.Log("Identifying brands and models…");
            var subject = state.Strategy!.Subject is { Length: > 0 } s ? s : state.Query;
            state.Products = await state.Agent.BuildProductSearchResultAsync(
                subject, state.GeoProfile, state.Bundle, state.Summary, context.CancellationToken);

            if (state.Products is { } p)
            {
                state.Log($"Extracted {p.ProductCount} product(s) from {p.BrandCount} brand(s).");
            }
        }
    }
}

/// <summary>
/// Step 5 — the brand/store enrichment loops. For each extracted brand and store (up to a per-kind
/// cap) we join against saved Context.dev/Places profiles, researching on a miss. Each loop streams
/// per-item progress so the user sees the work happening, and the verified store fields (Google
/// rating, address, phone, Maps link) flow into the rendered results.
/// </summary>
[Activity("Daleel", "Search", "Enrich with saved Brand/Store profiles from the database")]
public sealed class EnrichWithProfilesActivity : CodeActivity
{
    // Per-kind caps so a brand/store-heavy query can't fan out into unbounded research cost.
    private const int MaxBrands = 15;
    private const int MaxStores = 10;

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var state = context.GetRequiredService<SearchPipelineState>();
        if (state.FromCache || state.Products is null)
        {
            return;
        }

        var brandSvc = context.GetRequiredService<IBrandProfileService>();
        var storeSvc = context.GetRequiredService<IStoreProfileService>();
        var ct = context.CancellationToken;
        var products = state.Products;

        var brandsToBuild = Math.Min(products.Brands.Count, MaxBrands);
        var storesToBuild = Math.Min(products.Stores.Count, MaxStores);
        if (brandsToBuild + storesToBuild > 0)
        {
            state.Log($"Building profiles for {brandsToBuild} brand(s) and {storesToBuild} store(s)…");
        }

        // ── Brand loop ──────────────────────────────────────────────────────────
        var brands = new List<BrandInfo>(products.Brands.Count);
        foreach (var (b, i) in products.Brands.Select((b, i) => (b, i)))
        {
            Data.Brand? saved = null;
            if (i < MaxBrands)
            {
                state.Log($"Building profile for {b.Name}…");
                saved = await SafeGetBrand(brandSvc, b.Name, state.Geo, ct);
                state.RecordEvent(EventCategory.Profile, "profile.brand", "profile",
                    success: saved is not null,
                    metadata: new Dictionary<string, object?> { ["name"] = b.Name, ["found"] = saved is not null });
            }

            brands.Add(saved is null
                ? b
                : b with { Reputation = b.Reputation ?? ToReputation(saved), Url = b.Url ?? saved.Website });
        }

        // ── Store loop ──────────────────────────────────────────────────────────
        var stores = new List<StoreInfo>(products.Stores.Count);
        var verified = 0;
        foreach (var (s, i) in products.Stores.Select((s, i) => (s, i)))
        {
            Data.Store? saved = null;
            if (i < MaxStores)
            {
                state.Log($"Verifying {s.Name} on Google Maps…");
                saved = await SafeGetStore(storeSvc, s.Name, state.Geo, ct);
                state.RecordEvent(EventCategory.Profile, "profile.store", "profile",
                    success: saved is not null,
                    metadata: new Dictionary<string, object?>
                    {
                        ["name"] = s.Name,
                        ["found"] = saved is not null,
                        ["verified"] = saved?.IsVerified ?? false
                    });
            }

            if (saved is null)
            {
                stores.Add(s);
                continue;
            }

            if (saved.IsVerified)
            {
                verified++;
            }

            // Prefer the live result's own fields; backfill from the verified profile (Google data first).
            stores.Add(s with
            {
                Rating = s.Rating ?? saved.GoogleRating ?? saved.Rating,
                ReviewCount = s.ReviewCount ?? saved.GoogleReviewCount,
                Address = s.Address ?? saved.Address ?? saved.Location,
                Phone = s.Phone ?? saved.Phone,
                Latitude = s.Latitude ?? saved.Latitude,
                Longitude = s.Longitude ?? saved.Longitude,
                Url = s.Url ?? saved.Website ?? saved.GoogleMapsUrl
            });
        }

        state.Products = products with { Brands = brands, Stores = stores };

        var enriched = brands.Count(b => b.Reputation is not null);
        if (enriched > 0 || verified > 0)
        {
            state.Log($"Enriched {enriched} brand(s); verified {verified} store(s) on Google Maps.");
        }
    }

    private static async Task<Data.Brand?> SafeGetBrand(
        IBrandProfileService svc, string name, string geo, CancellationToken ct)
    {
        try { return await svc.GetOrCreateAsync(name, geo, ct); }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private static async Task<Data.Store?> SafeGetStore(
        IStoreProfileService svc, string name, string geo, CancellationToken ct)
    {
        try { return await svc.GetOrCreateAsync(name, geo, ct); }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    /// <summary>Maps a saved 0–10 brand profile onto the UI's 1–5 <see cref="BrandReputation"/> shape.</summary>
    private static BrandReputation ToReputation(Data.Brand saved) => new()
    {
        Brand = saved.Name,
        Score = saved.ReputationScore is { } r ? Math.Clamp(r / 2.0, 0, 5) : null,
        Pros = saved.Pros,
        Complaints = saved.Cons,
        Summary = saved.Description
    };
}

/// <summary>Step 6 — assemble the final answer object from the summary, bundle and (enriched) products.</summary>
[Activity("Daleel", "Search", "Aggregate: assemble the final ranked answer + result count")]
public sealed class AggregateResultsActivity : CodeActivity
{
    protected override ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var state = context.GetRequiredService<SearchPipelineState>();
        if (state.FromCache)
        {
            return ValueTask.CompletedTask;
        }

        var bundle = state.Bundle ?? new ResearchBundle();
        state.Answer = new AgentAnswer
        {
            Question = state.Query,
            Geo = state.GeoProfile?.Key ?? state.Geo,
            QueryType = state.Strategy?.QueryType ?? QueryType.General,
            Summary = state.Summary,
            Research = bundle,
            Products = state.Products,
            GeneratedAt = DateTimeOffset.UtcNow
        };
        state.ResultCount = state.Products?.ProductCount
            ?? bundle.WebResults.Count + bundle.ShoppingResults.Count;

        if (state.Products is { } p)
        {
            state.Log($"Ranking {p.ProductCount} product(s) from {p.BrandCount} brand(s)…");
        }
        return ValueTask.CompletedTask;
    }
}

/// <summary>Step 7 — record the halal-filter outcome (filtering itself happens at the gather chokepoint).</summary>
[Activity("Daleel", "Search", "Moderate: record the halal content-filter audit outcome")]
public sealed class ModerateContentActivity : CodeActivity
{
    protected override ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var state = context.GetRequiredService<SearchPipelineState>();
        if (state.FromCache)
        {
            return ValueTask.CompletedTask;
        }

        state.Log("Reviewing content quality…");
        var audit = state.Agent.ContentFilter.AuditLog;
        state.FilteredCount = audit.Count;
        state.FilteredCategories = string.Join(",", audit
            .Select(a => a.Contains(':') ? a[(a.IndexOf(':') + 1)..] : a)
            .Distinct());
        return ValueTask.CompletedTask;
    }
}

/// <summary>Step 8 — serialize the report and store it under the result key for the next identical search.</summary>
[Activity("Daleel", "Search", "Cache: serialize + persist the completed report")]
public sealed class CacheResultsActivity : CodeActivity
{
    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var state = context.GetRequiredService<SearchPipelineState>();
        if (state.FromCache || state.Answer is null)
        {
            return;
        }

        state.Log("Saving results…");
        state.ResultJson = ResultSerialization.Serialize(state.Answer);
        state.ResultType = "ask";
        state.RecordEvent(EventCategory.Cache, "cache.write", "cache");

        if (state.Cache is not null)
        {
            try
            {
                var cached = new CachedSearchResult(
                    state.ResultJson, state.ResultType, state.FilteredCount, state.FilteredCategories);
                await state.Cache.SetAsync(
                    state.ResultKey, JsonSerializer.Serialize(cached), state.CacheTtl, context.CancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw; // cancellation must propagate; only swallow genuine cache-write failures
            }
            catch
            {
                // Best-effort: a cache write must never fail the search.
            }
        }
    }
}

/// <summary>Step 9 — terminal marker; outputs are already on the state (cache hit or fresh run).</summary>
[Activity("Daleel", "Search", "Return: finalize and surface the result")]
public sealed class ReturnResultsActivity : CodeActivity
{
    protected override ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var state = context.GetRequiredService<SearchPipelineState>();
        if (state.FromCache)
        {
            state.Log("Loaded a saved answer.");
        }
        else if (state.Products is { } p && p.ProductCount > 0)
        {
            state.Log($"Done! Found {p.ProductCount} product(s) from {p.BrandCount} brand(s).");
        }
        else
        {
            state.Log("Done!");
        }
        return ValueTask.CompletedTask;
    }
}
