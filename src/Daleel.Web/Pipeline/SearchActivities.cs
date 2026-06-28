using System.Text.Json;
using Daleel.Agent;
using Daleel.Core.Geo;
using Daleel.Core.Intelligence;
using Daleel.Core.Models;
using Daleel.Web.Events;
using Daleel.Web.Pipeline.SubWorkflows;
using Daleel.Web.Profiles;
using Daleel.Web.Services;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Microsoft.Extensions.DependencyInjection;

namespace Daleel.Web.Pipeline;

// The eleven steps of the search pipeline, each a discrete Elsa CodeActivity. They share the scoped
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
        var services = context.GetRequiredService<SearchPipelineServices>();
        services.Report(SearchStep.Analyzing, "Progress.Msg.Analyzing");
        // The query itself is the strongest market signal ("AC in Dubai" → UAE), so it overrides the
        // stored/auto-detected default. Fall back to the request's geo when the query names no market.
        state.GeoProfile = GeoProfiles.DetectInText(state.Query) ?? GeoProfiles.ResolveOrDefault(state.Geo);
        state.Geo = state.GeoProfile.Key;
        services.Report(SearchStep.Analyzing, "Progress.Msg.Market", state.GeoProfile.Country);
        state.Strategy = await services.Agent.PlanAsync(
            PromptTemplates.PlanFreeform(state.Query, state.GeoProfile), context.CancellationToken);

        var subject = state.Strategy.Subject is { Length: > 0 } s ? s : state.Query;
        // The query-type noun is itself translatable: passing it as a "$"-prefixed resource key tells
        // the UI to localize it before slotting it into the "Looking for {0}: {1}" line.
        services.Report(SearchStep.Analyzing, "Progress.Msg.LookingFor", "$" + NounKey(state.Strategy.QueryType), subject);
    }

    /// <summary>Resource key for the friendly query-type noun, localized client-side.</summary>
    private static string NounKey(QueryType type) => type switch
    {
        QueryType.ProductResearch => "Progress.Noun.Products",
        QueryType.BrandLookup => "Progress.Noun.Brand",
        QueryType.StoreFinder => "Progress.Noun.Stores",
        QueryType.DealHunter => "Progress.Noun.Deals",
        QueryType.OpinionAggregation => "Progress.Noun.Opinions",
        QueryType.Comparison => "Progress.Noun.Comparison",
        _ => "Progress.Noun.Answers"
    };
}

/// <summary>Step 2 — replay a stored report for an identical recent search, short-circuiting the rest.</summary>
[Activity("Daleel", "Search", "Check the result cache and short-circuit on a hit")]
public sealed class CheckCacheActivity : CodeActivity
{
    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var state = context.GetRequiredService<SearchPipelineState>();
        var services = context.GetRequiredService<SearchPipelineServices>();
        if (services.Cache is null)
        {
            return;
        }

        services.Report(SearchStep.CheckingVault, "Progress.Msg.Vault");
        try
        {
            var payload = await services.Cache.GetAsync(state.ResultKey, context.CancellationToken);
            if (payload is null)
            {
                state.RecordEvent(EventCategory.Cache, "cache.miss", "cache");
                return;
            }

            var cached = JsonSerializer.Deserialize<CachedSearchResult>(payload);
            if (cached is null)
            {
                state.RecordEvent(EventCategory.Cache, "cache.miss", "cache");
                return;
            }

            // Smart cache validation: a hit isn't automatically good enough. Score the stored report for
            // completeness and let the verdict decide — replay it, replay-then-refill-in-background, or
            // (when it's too thin) discard it and fall through to a full live search.
            var validator = context.GetRequiredService<ICacheQualityValidator>();
            var quality = ScoreQuality(validator, cached.ResultJson);

            if (quality.Decision == CacheDecision.Miss)
            {
                // Too incomplete to count as a hit — fall through to a full live search, exactly like a
                // miss. FromCache stays false, so every downstream activity runs.
                state.RecordEvent(EventCategory.Cache, "cache.stale", "cache", success: false,
                    metadata: QualityMetadata(quality));
                return;
            }

            state.FromCache = true;
            state.ResultJson = cached.ResultJson;
            state.ResultType = cached.ResultType;
            state.FilteredCount = cached.FilteredCount;
            state.FilteredCategories = cached.FilteredCategories ?? string.Empty;
            state.CacheQuality = quality;

            if (quality.Decision == CacheDecision.ServeAndEnrich)
            {
                // Show the cached report immediately; the runner reads CacheQuality back and kicks off a
                // background pass that re-scrapes ONLY the missing pieces, then streams the refreshed result.
                state.RecordEvent(EventCategory.Cache, "cache.partial", "cache",
                    metadata: QualityMetadata(quality));
            }
            else
            {
                state.RecordEvent(EventCategory.Cache, "cache.hit", "cache",
                    metadata: QualityMetadata(quality));
            }
            services.Report(SearchStep.CheckingVault, "Progress.Msg.CacheHit");
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

    /// <summary>
    /// Scores the cached report for completeness. Must use <see cref="ResultSerialization"/> (not raw
    /// <c>JsonSerializer</c>) so the answer's enum fields deserialize the same way they were written. A
    /// corrupt or unscoreable payload defaults to <see cref="CacheQualityReport.Complete"/> — better to
    /// replay a hit than to needlessly re-run a search over a scoring hiccup.
    /// </summary>
    private static CacheQualityReport ScoreQuality(ICacheQualityValidator validator, string resultJson)
    {
        try
        {
            return ResultSerialization.Deserialize<AgentAnswer>(resultJson) is { } answer
                ? validator.Evaluate(answer)
                : CacheQualityReport.Complete;
        }
        catch
        {
            return CacheQualityReport.Complete;
        }
    }

    /// <summary>Flattens a quality verdict into event metadata for the analytics/event store.</summary>
    private static Dictionary<string, object?> QualityMetadata(CacheQualityReport quality) => new()
    {
        ["score"] = quality.Score,
        ["decision"] = quality.Decision.ToString(),
        ["missing"] = string.Join("; ", quality.Missing)
    };
}

/// <summary>
/// Step 2b — the "thinking" step: for a product query, ask the LLM to analyse the category BEFORE any
/// sources are gathered (product type, relevant store types, expected brands, the comparison specs, a
/// price expectation). The resulting <see cref="SearchIntelligence"/> is threaded into extraction
/// (schema-aware) and onto the final result. No-ops on a cache hit or a non-product query.
/// </summary>
[Activity("Daleel", "Search", "Analyze the category: product type, relevant stores, expected brands, comparison specs")]
public sealed class AnalyzeMarketActivity : CodeActivity
{
    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var state = context.GetRequiredService<SearchPipelineState>();
        var services = context.GetRequiredService<SearchPipelineServices>();
        if (state.FromCache || !state.IsProductQuery || state.GeoProfile is null || state.Strategy is null)
        {
            return;
        }

        var category = state.Strategy.Subject is { Length: > 0 } s ? s : state.Query;
        // "Analyzing AC market requirements…" — surface the up-front reasoning to the user.
        services.Report(SearchStep.Analyzing, "Progress.Msg.Analyzing");
        services.Report(SearchStep.Analyzing, "Progress.Msg.AnalyzingCategory", category);

        var intel = await services.Agent.AnalyzeCategoryAsync(category, state.GeoProfile, context.CancellationToken);
        state.Intelligence = intel;

        if (intel is { IsEmpty: false })
        {
            // "Looking for electronics and HVAC stores…"
            if (intel.RelevantStoreTypes.Count > 0)
            {
                services.Report(SearchStep.Analyzing, "Progress.Msg.LookingForStores",
                    string.Join(", ", intel.RelevantStoreTypes.Take(3)));
            }

            // "Extracting BTU, energy rating, cooling specs…"
            if (!intel.Schema.IsEmpty)
            {
                var keySpecs = intel.Schema.Fields
                    .Where(f => f.Importance == SpecImportance.Key)
                    .Select(f => f.Label)
                    .ToList();
                if (keySpecs.Count == 0)
                {
                    keySpecs = intel.Schema.Fields.Select(f => f.Label).ToList();
                }

                if (keySpecs.Count > 0)
                {
                    services.Report(SearchStep.Analyzing, "Progress.Msg.ExtractingSpecs",
                        string.Join(", ", keySpecs.Take(4)));
                }
            }

            state.RecordEvent(
                "intelligence", "category.analyzed", "llm",
                metadata: new Dictionary<string, object?>
                {
                    ["productType"] = intel.ProductType,
                    ["brands"] = intel.ExpectedBrands.Count,
                    ["specs"] = intel.Schema.Fields.Count
                });
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
        var services = context.GetRequiredService<SearchPipelineServices>();
        if (state.FromCache || state.Strategy is null || state.GeoProfile is null)
        {
            return;
        }

        services.Report(SearchStep.SearchingWeb, "Progress.Msg.Expanding");
        state.Bundle = await services.Agent.GatherAsync(state.Strategy, state.GeoProfile, context.CancellationToken);

        var b = state.Bundle;
        services.Report(SearchStep.SearchingWeb, "Progress.Msg.Gathered",
            b.WebResults.Count, b.ShoppingResults.Count, b.Stores.Count);
    }
}

/// <summary>Step 4 — the LLM structured pass: analyst summary plus product extraction for product queries.</summary>
[Activity("Daleel", "Search", "Extract products: LLM analyst summary + structured product projection")]
public sealed class ExtractProductsActivity : CodeActivity
{
    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var state = context.GetRequiredService<SearchPipelineState>();
        var services = context.GetRequiredService<SearchPipelineServices>();
        if (state.FromCache || state.Bundle is null || state.GeoProfile is null)
        {
            return;
        }

        services.Report(SearchStep.ExtractingProducts, "Progress.Msg.Reading", state.Bundle.Sources.Count);
        var system = state.IsProductQuery ? PromptTemplates.ProductAnalystSystem : null;
        state.Summary = await services.Agent.AnalyzeAsync(
            state.Query, state.GeoProfile, state.Bundle, context.CancellationToken, system);

        if (state.IsProductQuery)
        {
            services.Report(SearchStep.ExtractingProducts, "Progress.Msg.Identifying");
            var subject = state.Strategy!.Subject is { Length: > 0 } s ? s : state.Query;
            // Pass the up-front category intelligence so extraction is schema-aware (fills the spec
            // keys that matter for this product type) and the schema rides along onto the result.
            state.Products = await services.Agent.BuildProductSearchResultAsync(
                subject, state.GeoProfile, state.Bundle, state.Summary, context.CancellationToken,
                intelligence: state.Intelligence);

            if (state.Products is { } p)
            {
                services.Report(SearchStep.ExtractingProducts, "Progress.Msg.Extracted", p.ProductCount, p.BrandCount);
            }
        }
    }
}

// ── Steps 5–7: dispatch one sub-workflow per entity, in bounded parallel ─────────────────────────
// The flat brand/store enrichment loop became three dispatch activities. Each fans a per-entity Elsa
// sub-workflow (BrandResearch / StoreResearch / ItemDeepDive) out across its entities — each child in
// its OWN DI scope (hence its own DbContext, so the fan-out is concurrency-safe) with a hard per-entity
// timeout. The enriched entities + buffered events flow back onto the shared SearchPipelineState.

/// <summary>Step 5 — research every found brand in parallel (one <see cref="BrandResearchWorkflow"/> each).</summary>
[Activity("Daleel", "Search", "Dispatch a brand-research sub-workflow per brand, in parallel")]
public sealed class DispatchBrandWorkflowsActivity : CodeActivity
{
    /// <summary>Cap so a brand-heavy query can't fan out into unbounded research cost.</summary>
    private const int MaxBrands = 15;

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var state = context.GetRequiredService<SearchPipelineState>();
        var services = context.GetRequiredService<SearchPipelineServices>();
        if (state.FromCache || state.Products is not { Brands.Count: > 0 } products)
        {
            return;
        }

        var scopeFactory = context.GetRequiredService<IServiceScopeFactory>();
        var dispatched = products.Brands.Take(MaxBrands).ToList();
        var rest = products.Brands.Skip(MaxBrands).ToList();

        // Advance the stepper to the brand-profile phase (PR #10's animation) before fanning out.
        services.Report(SearchStep.BuildingProfiles, "Progress.Msg.BuildingProfiles", dispatched.Count, products.Stores.Count);
        var results = await SubWorkflowDispatcher
            .RunManyAsync<BrandResearchWorkflow, BrandResearchState, BrandInfo>(
                scopeFactory, dispatched,
                (s, svc, brand) =>
                {
                    svc.Agent = services.Agent;
                    svc.Progress = services.Progress;
                    s.Geo = state.Geo;
                    s.SearchId = state.SearchId;
                    s.Brand = brand;
                    s.Result = brand;
                },
                services.Progress,
                SubWorkflowDispatcher.DefaultTimeout, context.CancellationToken);

        var merged = results.Select(r => r.Result).Concat(rest).ToList();
        foreach (var r in results)
        {
            state.Events.AddRange(r.Events);
        }

        state.Products = products with { Brands = merged };
        var enriched = merged.Count(b => b.Reputation is not null);
        if (enriched > 0)
        {
            services.Report(SearchStep.BuildingProfiles, "Progress.Msg.EnrichedBrandsCount", enriched);
        }
    }
}

/// <summary>Step 6 — research every found store in parallel (one <see cref="StoreResearchWorkflow"/> each).</summary>
[Activity("Daleel", "Search", "Dispatch a store-research sub-workflow per store, in parallel")]
public sealed class DispatchStoreWorkflowsActivity : CodeActivity
{
    /// <summary>Cap so a store-heavy query can't fan out into unbounded research cost.</summary>
    private const int MaxStores = 10;

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var state = context.GetRequiredService<SearchPipelineState>();
        var services = context.GetRequiredService<SearchPipelineServices>();
        if (state.FromCache || state.Products is not { Stores.Count: > 0 } products)
        {
            return;
        }

        var scopeFactory = context.GetRequiredService<IServiceScopeFactory>();
        var dispatched = products.Stores.Take(MaxStores).ToList();
        var rest = products.Stores.Skip(MaxStores).ToList();

        // Advance the stepper to the store-verification phase (PR #10's animation) before fanning out.
        services.Report(SearchStep.FindingStores, "Progress.Msg.VerifyingStore", dispatched.Count);
        var results = await SubWorkflowDispatcher
            .RunManyAsync<StoreResearchWorkflow, StoreResearchState, StoreInfo>(
                scopeFactory, dispatched,
                (s, svc, store) =>
                {
                    svc.Agent = services.Agent;
                    svc.Progress = services.Progress;
                    s.Geo = state.Geo;
                    s.SearchId = state.SearchId;
                    s.Store = store;
                    s.Result = store;
                },
                services.Progress,
                SubWorkflowDispatcher.DefaultTimeout, context.CancellationToken);

        var merged = results.Select(r => r.Result).Concat(rest).ToList();
        foreach (var r in results)
        {
            state.Events.AddRange(r.Events);
        }

        state.Products = products with { Stores = merged };
        var verified = merged.Count(s => s.Rating is not null);
        if (verified > 0)
        {
            services.Report(SearchStep.FindingStores, "Progress.Msg.VerifiedStoresCount", verified);
        }
    }
}

/// <summary>Step 7 — deep-dive every found product/model in parallel (one <see cref="ItemDeepDiveWorkflow"/> each).</summary>
[Activity("Daleel", "Search", "Dispatch an item deep-dive sub-workflow per product/model, in parallel")]
public sealed class DispatchItemWorkflowsActivity : CodeActivity
{
    /// <summary>Cap so a model-heavy query can't fan out into unbounded scrape cost.</summary>
    private const int MaxItems = 20;

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var state = context.GetRequiredService<SearchPipelineState>();
        var services = context.GetRequiredService<SearchPipelineServices>();
        if (state.FromCache || !state.IsProductQuery || state.Products is not { Models.Count: > 0 } products)
        {
            return;
        }

        var scopeFactory = context.GetRequiredService<IServiceScopeFactory>();
        var dispatched = products.Models.Take(MaxItems).ToList();
        var rest = products.Models.Skip(MaxItems).ToList();

        services.Report(SearchStep.ComparingPrices, "Progress.Msg.DeepDiving", dispatched.Count);
        var results = await SubWorkflowDispatcher
            .RunManyAsync<ItemDeepDiveWorkflow, ItemDeepDiveState, ProductModel>(
                scopeFactory, dispatched,
                (s, svc, model) =>
                {
                    svc.Agent = services.Agent;
                    svc.Progress = services.Progress;
                    s.Geo = state.Geo;
                    s.SearchId = state.SearchId;
                    s.Model = model;
                    s.Result = model;
                    // Thread the category schema through so the per-item spec merge can order/rename
                    // recognized fields against it (the right attributes per category, not a blind dump).
                    s.Schema = products.Schema;
                },
                services.Progress,
                SubWorkflowDispatcher.DefaultTimeout, context.CancellationToken);

        var merged = results.Select(r => r.Result).Concat(rest).ToList();
        foreach (var r in results)
        {
            state.Events.AddRange(r.Events);
        }

        state.Products = products with { Models = merged };
    }
}

/// <summary>Step 8 — assemble the final answer object from the summary, bundle and (enriched) products.</summary>
[Activity("Daleel", "Search", "Aggregate: assemble the final ranked answer + result count")]
public sealed class AggregateResultsActivity : CodeActivity
{
    protected override ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var state = context.GetRequiredService<SearchPipelineState>();
        var services = context.GetRequiredService<SearchPipelineServices>();
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
            services.Report(SearchStep.ComparingPrices, "Progress.Msg.Ranking", p.ProductCount, p.BrandCount);
        }
        return ValueTask.CompletedTask;
    }
}

/// <summary>Step 9 — record the halal-filter outcome (filtering itself happens at the gather chokepoint).</summary>
[Activity("Daleel", "Search", "Moderate: record the halal content-filter audit outcome")]
public sealed class ModerateContentActivity : CodeActivity
{
    protected override ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var state = context.GetRequiredService<SearchPipelineState>();
        var services = context.GetRequiredService<SearchPipelineServices>();
        if (state.FromCache)
        {
            return ValueTask.CompletedTask;
        }

        services.Report(SearchStep.ComparingPrices, "Progress.Msg.Reviewing");
        var audit = services.Agent.ContentFilter.AuditLog;
        state.FilteredCount = audit.Count;
        state.FilteredCategories = string.Join(",", audit
            .Select(a => a.Contains(':') ? a[(a.IndexOf(':') + 1)..] : a)
            .Distinct());
        return ValueTask.CompletedTask;
    }
}

/// <summary>Step 10 — serialize the report and store it under the result key for the next identical search.</summary>
[Activity("Daleel", "Search", "Cache: serialize + persist the completed report")]
public sealed class CacheResultsActivity : CodeActivity
{
    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var state = context.GetRequiredService<SearchPipelineState>();
        var services = context.GetRequiredService<SearchPipelineServices>();
        if (state.FromCache || state.Answer is null)
        {
            return;
        }

        // Never cache an empty product search. A zero-result payload is too often a transient/upstream
        // failure (provider outage, geo-filter bug) rather than a true "nothing exists" — persisting it
        // would serve the stale empty for the whole TTL and hide the recovery. A non-product "ask" answer
        // (Products is null) is a legitimate result and is still cached.
        if (IsEmptyProductResult(state.Answer))
        {
            state.RecordEvent(EventCategory.Cache, "cache.skip-empty", "cache");
            return;
        }

        services.Report(SearchStep.ComparingPrices, "Progress.Msg.Saving");
        state.ResultJson = ResultSerialization.Serialize(state.Answer);
        state.ResultType = "ask";
        state.RecordEvent(EventCategory.Cache, "cache.write", "cache");

        if (services.Cache is not null)
        {
            try
            {
                var cached = new CachedSearchResult(
                    state.ResultJson, state.ResultType, state.FilteredCount, state.FilteredCategories);
                await services.Cache.SetAsync(
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

    /// <summary>
    /// True for a product search that came back with nothing. A non-product "ask" answer (Products is
    /// null) is a legitimate result, not an empty one, so it is still cacheable.
    /// </summary>
    private static bool IsEmptyProductResult(AgentAnswer answer) =>
        answer.Products is { } products
        && products.Models.Count == 0 && products.Brands.Count == 0 && products.Stores.Count == 0;
}

/// <summary>Step 11 — terminal marker; outputs are already on the state (cache hit or fresh run).</summary>
[Activity("Daleel", "Search", "Return: finalize and surface the result")]
public sealed class ReturnResultsActivity : CodeActivity
{
    protected override ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var state = context.GetRequiredService<SearchPipelineState>();
        var services = context.GetRequiredService<SearchPipelineServices>();
        state.CompletedAt = DateTimeOffset.UtcNow;
        if (state.FromCache)
        {
            services.Report(SearchStep.Done, "Progress.Msg.LoadedSaved");
        }
        else if (state.Products is { } p && p.ProductCount > 0)
        {
            services.Report(SearchStep.Done, "Progress.Msg.DoneCount", p.ProductCount, p.BrandCount);
        }
        else
        {
            services.Report(SearchStep.Done, "Progress.Msg.Done");
        }
        return ValueTask.CompletedTask;
    }
}
