using System.Text.Json;
using Daleel.Agent;
using Daleel.Core.Geo;
using Daleel.Core.Models;
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
        state.GeoProfile = GeoProfiles.ResolveOrDefault(state.Geo);
        state.Log($"Planning research for: {state.Query} [{state.GeoProfile.Country}]");
        state.Strategy = await state.Agent.PlanAsync(
            PromptTemplates.PlanFreeform(state.Query, state.GeoProfile), context.CancellationToken);
    }
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

        try
        {
            var payload = await state.Cache.GetAsync(state.ResultKey, context.CancellationToken);
            if (payload is null)
            {
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
                state.Log("⚡ Loaded from cache — identical search run recently.");
            }
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

        state.Bundle = await state.Agent.GatherAsync(state.Strategy, state.GeoProfile, context.CancellationToken);
        state.Log("Gathered web, shopping, store, social and page sources.");
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

        var system = state.IsProductQuery ? PromptTemplates.ProductAnalystSystem : null;
        state.Summary = await state.Agent.AnalyzeAsync(
            state.Query, state.GeoProfile, state.Bundle, context.CancellationToken, system);

        if (state.IsProductQuery)
        {
            var subject = state.Strategy!.Subject is { Length: > 0 } s ? s : state.Query;
            state.Products = await state.Agent.BuildProductSearchResultAsync(
                subject, state.GeoProfile, state.Bundle, state.Summary, context.CancellationToken);
        }
    }
}

/// <summary>Step 5 — join the extracted brands/stores against saved profiles (DB-first, research on miss).</summary>
[Activity("Daleel", "Search", "Enrich with saved Brand/Store profiles from the database")]
public sealed class EnrichWithProfilesActivity : CodeActivity
{
    /// <summary>Cap on inline profile lookups per search so a brand-heavy query can't fan out unbounded.</summary>
    private const int MaxEnrich = 12;

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

        var brands = new List<BrandInfo>(products.Brands.Count);
        foreach (var (b, i) in products.Brands.Select((b, i) => (b, i)))
        {
            var saved = i < MaxEnrich ? await SafeGetBrand(brandSvc, b.Name, state.Geo, ct) : null;
            brands.Add(saved is null
                ? b
                : b with { Reputation = b.Reputation ?? ToReputation(saved), Url = b.Url ?? saved.Website });
        }

        var stores = new List<StoreInfo>(products.Stores.Count);
        foreach (var (s, i) in products.Stores.Select((s, i) => (s, i)))
        {
            var saved = i < MaxEnrich ? await SafeGetStore(storeSvc, s.Name, state.Geo, ct) : null;
            stores.Add(saved is null
                ? s
                : s with { Rating = s.Rating ?? saved.Rating, Address = s.Address ?? saved.Location });
        }

        state.Products = products with { Brands = brands, Stores = stores };
        var enriched = brands.Count(b => b.Reputation is not null);
        if (enriched > 0)
        {
            state.Log($"Enriched {enriched} brand(s) from saved Context.dev profiles.");
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

        state.ResultJson = ResultSerialization.Serialize(state.Answer);
        state.ResultType = "ask";

        if (state.Cache is not null)
        {
            try
            {
                var cached = new CachedSearchResult(
                    state.ResultJson, state.ResultType, state.FilteredCount, state.FilteredCategories);
                await state.Cache.SetAsync(
                    state.ResultKey, JsonSerializer.Serialize(cached), state.CacheTtl, context.CancellationToken);
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
        state.Log(state.FromCache ? "Returned cached result." : "Search complete.");
        return ValueTask.CompletedTask;
    }
}
