using System.Text.Json;
using Daleel.Agent;
using Daleel.Core.Geo;
using Daleel.Core.Intelligence;
using Daleel.Core.Models;
using Daleel.Web.Events;
using Daleel.Web.Persistence;
using Daleel.Web.Pipeline.SubWorkflows;
using Daleel.Web.Profiles;
using Daleel.Web.Services;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Daleel.Web.Pipeline;

// The eleven steps of the search pipeline, each a discrete Elsa CodeActivity. They share the scoped
// SearchPipelineState (resolved from the activity's DI context) and delegate the heavy lifting to the
// existing, well-tested AgentService stages — so the workflow owns orchestration without
// reimplementing plan/gather/analyze/project. Every post-cache activity no-ops on a cache hit.

/// <summary>Step 1 — normalize the query: resolve the market and run the LLM planner into a strategy.</summary>
[Activity("Daleel", "Search", "Plan: resolve market + expand the query into a bilingual search strategy")]
public sealed class ParseQueryActivity : CancellableActivity
{
    protected override async ValueTask DoExecuteAsync(ActivityExecutionContext context)
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
            PromptTemplates.PlanFreeform(state.Query, state.GeoProfile), context.CancellationToken,
            // The raw query lets PlanAsync's deterministic buy-intent backstop correct a planner
            // misclassification (General on a "best <product>" query would skip the whole grid).
            userQuery: state.Query);
        // Carry the planner's thing-type classification onto the run state so the extraction activity
        // can pick the product / service / place prompt without reaching back into the strategy.
        state.Intent = state.Strategy.Intent;

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
public sealed class CheckCacheActivity : CancellableActivity
{
    protected override async ValueTask DoExecuteAsync(ActivityExecutionContext context)
    {
        var state = context.GetRequiredService<SearchPipelineState>();
        var services = context.GetRequiredService<SearchPipelineServices>();
        if (services.Cache is null)
        {
            return;
        }

        // Admin kill-switch: when the search cache is disabled at /admin/settings, skip the check so
        // every search runs fresh. FromCache stays false, so all downstream activities execute live.
        // Resolve OPTIONALLY (GetService, like the base cancel check): a DI context without the config
        // service — e.g. the workflow unit tests — must fail open to normal caching, never fault.
        if (context.GetService<Daleel.Web.Data.ISystemConfigService>() is { } config &&
            !await config.GetBoolAsync("cache.search_enabled", true, context.CancellationToken))
        {
            state.RecordEvent(EventCategory.Cache, "cache.bypass", "cache");
            return;
        }

        // The whole-query result VAULT was removed (owner decision): a cached whole-result would serve
        // stale products/prices and skip the relevance/learning pass, so every search now runs fresh. The
        // PROVIDER cache (raw discovery-API responses) and the ENTITY caches (ProductProfile item-meta,
        // ScrapedPrice price series, Brand/Store meta) are untouched — only the query→whole-grid vault is
        // gone. FromCache stays false, so every downstream activity runs live.
        _ = services;
        state.RecordEvent(EventCategory.Cache, "cache.bypass", "cache");
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
public sealed class AnalyzeMarketActivity : CancellableActivity
{
    protected override async ValueTask DoExecuteAsync(ActivityExecutionContext context)
    {
        var state = context.GetRequiredService<SearchPipelineState>();
        var services = context.GetRequiredService<SearchPipelineServices>();
        if (state.FromCache || !state.IsProductQuery || state.GeoProfile is null || state.Strategy is null)
        {
            return;
        }

        var category = state.Strategy.Subject is { Length: > 0 } s ? s : state.Query;
        // Surface the up-front reasoning to the user (renders as e.g. "Getting a feel for air conditioners…").
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
public sealed class GatherSourcesActivity : CancellableActivity
{
    protected override async ValueTask DoExecuteAsync(ActivityExecutionContext context)
    {
        var state = context.GetRequiredService<SearchPipelineState>();
        var services = context.GetRequiredService<SearchPipelineServices>();
        if (state.FromCache || state.Strategy is null || state.GeoProfile is null)
        {
            return;
        }

        services.Report(SearchStep.SearchingWeb, "Progress.Msg.Expanding");
        state.Bundle = await services.Agent.GatherAsync(state.Strategy, state.GeoProfile, context.CancellationToken);

        // LLM-ACTOR coverage (flag actor.gather, default off): after the fixed strategy runs, an LLM
        // looks at what came back and — like a researcher noticing a gap — proposes a few FOLLOW-UP
        // queries, which run once and merge in. Bounded (one LLM call + one gather round), ADDITIVE
        // (only ever adds results, never removes), so it can't regress a search. This is the synchronous
        // path, so it stays deliberately light; deep multi-turn research lives in the enrichment queue.
        var config = context.GetService<Daleel.Web.Data.ISystemConfigService>();
        if (config is not null &&
            await config.GetBoolAsync(Daleel.Web.Pipeline.Enrichment.Actor.ActorFlags.Gather, false, context.CancellationToken))
        {
            var extra = await CoverAsync(services.Agent, state, context.CancellationToken);
            if (extra is not null)
            {
                state.Bundle = MergeCoverage(state.Bundle!, extra);
            }
        }

        var b = state.Bundle;
        services.Report(SearchStep.SearchingWeb, "Progress.Msg.Gathered",
            b.WebResults.Count, b.ShoppingResults.Count, b.Stores.Count);
    }

    /// <summary>Proposes follow-up queries from the gathered results and runs ONE more gather round with them.</summary>
    private static async Task<ResearchBundle?> CoverAsync(
        Daleel.Agent.AgentService agent, SearchPipelineState state, CancellationToken ct)
    {
        var b = state.Bundle;
        if (b is null)
        {
            return null;
        }

        var titles = b.WebResults.Concat(b.ShoppingResults)
            .Select(r => r.Title).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct().Take(20).ToList();

        const string system =
            "You are a shopping researcher reviewing a search's first-round results. Propose UP TO 3 " +
            "additional web/shopping search queries that would surface RELEVANT sellers, models, or prices " +
            "this round likely MISSED (other local stores, specific model numbers, price pages). Stay on " +
            "the same product/topic. Return STRICT JSON only: {\"queries\":[\"...\"]}. Empty if coverage looks complete.";
        var user = "Search: " + state.Query + "\nMarket: " + (state.GeoProfile?.Country ?? state.Geo) +
            "\nResults so far:\n" + string.Join("\n", titles.Select(t => "- " + t));

        string text;
        try
        {
            text = await agent.SynthesizeAsync(system, user, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; } // coverage is best-effort — never fail the search

        var parsed = Daleel.Core.Llm.LlmJson.Deserialize<CoverageQueries>(text);
        var queries = (parsed?.Queries ?? new List<string>())
            .Where(q => !string.IsNullOrWhiteSpace(q)).Select(q => q.Trim()).Distinct().Take(3).ToList();
        if (queries.Count == 0 || state.Strategy is null || state.GeoProfile is null)
        {
            return null;
        }

        try
        {
            return await agent.GatherAsync(
                new Daleel.Core.Models.SearchStrategy { WebQueries = queries, ShoppingQueries = queries },
                state.GeoProfile, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private sealed record CoverageQueries(List<string>? Queries);

    /// <summary>Additive merge: appends the follow-up round's web/shopping results (deduped by URL) and prices/sources.</summary>
    private static ResearchBundle MergeCoverage(ResearchBundle primary, ResearchBundle extra)
    {
        static IReadOnlyList<Daleel.Search.Abstractions.SearchResult> Dedupe(
            IReadOnlyList<Daleel.Search.Abstractions.SearchResult> a,
            IReadOnlyList<Daleel.Search.Abstractions.SearchResult> b)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var merged = new List<Daleel.Search.Abstractions.SearchResult>();
            foreach (var r in a.Concat(b))
            {
                var key = r.Url ?? (r.Title + "|" + r.Seller);
                if (seen.Add(key)) merged.Add(r);
            }
            return merged;
        }

        return primary with
        {
            WebResults = Dedupe(primary.WebResults, extra.WebResults),
            ShoppingResults = Dedupe(primary.ShoppingResults, extra.ShoppingResults),
            Prices = primary.Prices.Concat(extra.Prices).ToList(),
            Sources = primary.Sources.Concat(extra.Sources).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        };
    }
}

/// <summary>Step 4 — the LLM structured pass: analyst summary plus product extraction for product queries.</summary>
[Activity("Daleel", "Search", "Extract products: LLM analyst summary + structured product projection")]
public sealed class ExtractProductsActivity : CancellableActivity
{
    protected override async ValueTask DoExecuteAsync(ActivityExecutionContext context)
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

        // Run the structured extraction pass for buy-intent product queries AND for any service/place
        // intent (those reuse the same structured shape via an intent-specific extraction prompt).
        if (state.IsProductQuery || state.Intent != SearchIntentType.Product)
        {
            services.Report(SearchStep.ExtractingProducts, "Progress.Msg.Identifying");
            var subject = state.Strategy!.Subject is { Length: > 0 } s ? s : state.Query;
            // Pass the up-front category intelligence so extraction is schema-aware (fills the spec
            // keys that matter for this product type) and the schema rides along onto the result. The
            // intent selects the product / service / place extraction prompt.
            state.Products = await services.Agent.BuildProductSearchResultAsync(
                subject, state.GeoProfile, state.Bundle, state.Summary, context.CancellationToken,
                intelligence: state.Intelligence, intent: state.Intent);

            if (state.Products is { } p)
            {
                services.Report(SearchStep.ExtractingProducts, "Progress.Msg.Extracted", p.ProductCount, p.BrandCount);
                // FIRST PARTIAL: the grid exists — show it NOW. The UI renders these products while the
                // enrichment fan-out below is still running; updates stream in as sub-workflows land.
                await services.TryPushPartialAsync(state);
                // Persist each surfaced entity as a self-contained JSON document in R2 (source of truth)
                // with a thin Postgres index row. Best-effort: persistence must never fail the search.
                await PersistEntitiesAsync(context, state, p);
            }
        }
    }

    /// <summary>
    /// Projects the extracted models into <see cref="Daleel.Core.Persistence.EntityDocument"/>s (one per
    /// model, intent-tagged, with every relation ID embedded) and writes each to R2 + the Postgres index
    /// via <see cref="ISearchEntityStore"/>. Wholly best-effort: any failure is logged and swallowed so a
    /// persistence hiccup never sinks the user's search.
    /// </summary>
    private static async Task PersistEntitiesAsync(
        ActivityExecutionContext context, SearchPipelineState state, ProductSearchResult products)
    {
        if (products.Models.Count == 0)
        {
            return;
        }

        try
        {
            var store = context.GetRequiredService<ISearchEntityStore>();
            var documents = EntityDocumentMapper.ToDocuments(
                products, state.Intent, state.SearchId, DateTimeOffset.UtcNow);
            var saved = await store.SaveAllAsync(documents, context.CancellationToken);
            state.RecordEvent(EventCategory.Profile, "entities.persisted", "r2",
                metadata: new Dictionary<string, object?> { ["count"] = saved, ["intent"] = state.Intent.ToString() });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            context.GetRequiredService<ILogger<ExtractProductsActivity>>()
                .LogWarning(ex, "Entity persistence failed.");
        }
    }
}

// ── Steps 5–7: one CONCURRENT dispatch of every enrichment sub-workflow, streaming results ───────
// The three sequential barrier stages (brands, then stores, then items) became one activity that fans
// ALL entities out at once — each child in its OWN DI scope (hence its own DbContext) with a hard
// per-entity timeout. Two properties matter:
//   • NO BARRIERS THE UI WAITS ON: every finished entity merges into state.Products immediately and a
//     throttled partial result streams to the user's devices — the grid fills in live while slower
//     entities are still being researched.
//   • NO CROSS-STAGE DEADLOCK: brands/stores/items proceed independently; a slow store can never delay
//     item deep-dives, and a wedged child is bounded by the dispatcher's per-entity timeout.

/// <summary>
/// Steps 5–7 — research every found brand, store and model CONCURRENTLY (one sub-workflow per entity),
/// merging each enriched entity into the shared state AS IT LANDS and streaming throttled partial
/// results to the user. The final state is identical to the old sequential stages; only the waiting is gone.
/// </summary>
[Activity("Daleel", "Search", "Dispatch brand/store/item sub-workflows concurrently, streaming results")]
public sealed class DispatchEnrichmentWorkflowsActivity : CancellableActivity
{
    /// <summary>Minimum interval between streamed partial pushes (the trailing push is unconditional).</summary>
    private static readonly TimeSpan PushInterval = TimeSpan.FromMilliseconds(800);

    protected override async ValueTask DoExecuteAsync(ActivityExecutionContext context)
    {
        var state = context.GetRequiredService<SearchPipelineState>();
        var services = context.GetRequiredService<SearchPipelineServices>();
        if (state.FromCache || state.Products is not { } products)
        {
            return;
        }

        var scopeFactory = context.GetRequiredService<IServiceScopeFactory>();

        // Slot arrays: each entity's enriched result replaces its input IN PLACE as its sub-workflow
        // lands; merges rebuild Products from the current slots plus any env-restrained overflow, so a
        // partial always contains every entity (enriched or not yet).
        var brandSlots = products.Brands.Take(PipelineLimits.MaxBrands).ToArray();
        var brandRest = products.Brands.Skip(PipelineLimits.MaxBrands).ToList();
        var storeSlots = products.Stores.Take(PipelineLimits.MaxStores).ToArray();
        var storeRest = products.Stores.Skip(PipelineLimits.MaxStores).ToList();
        var deepDiveItems = state.IsProductQuery;
        var itemSlots = deepDiveItems ? products.Models.Take(PipelineLimits.MaxItems).ToArray() : Array.Empty<ProductModel>();
        var itemRest = deepDiveItems ? products.Models.Skip(PipelineLimits.MaxItems).ToList() : new List<ProductModel>();

        // One gate serializes the quick in-memory merges (slot write + Products rebuild + event
        // append). Never held across an await — pushes happen outside it — so it cannot deadlock.
        var gate = new object();
        var lastPush = System.Diagnostics.Stopwatch.StartNew();

        void Merge()
        {
            state.Products = state.Products! with
            {
                Brands = brandSlots.Concat(brandRest).ToList(),
                Stores = storeSlots.Concat(storeRest).ToList(),
                Models = deepDiveItems ? itemSlots.Concat(itemRest).ToList() : state.Products!.Models
            };
        }

        async Task StreamAsync()
        {
            bool push;
            lock (gate)
            {
                push = lastPush.Elapsed >= PushInterval;
                if (push)
                {
                    lastPush.Restart();
                }
            }
            if (push)
            {
                await services.TryPushPartialAsync(state);
            }
        }

        // Advance the stepper through all three enrichment phases up-front — they genuinely run at once.
        if (brandSlots.Length > 0)
        {
            services.Report(SearchStep.BuildingProfiles, "Progress.Msg.BuildingProfiles", brandSlots.Length, storeSlots.Length);
        }
        if (storeSlots.Length > 0)
        {
            services.Report(SearchStep.FindingStores, "Progress.Msg.VerifyingStore", storeSlots.Length);
        }
        if (itemSlots.Length > 0)
        {
            services.Report(SearchStep.ComparingPrices, "Progress.Msg.DeepDiving", itemSlots.Length);
        }

        var brandTask = brandSlots.Length == 0
            ? Task.CompletedTask
            : SubWorkflowDispatcher.RunManyAsync<BrandResearchWorkflow, BrandResearchState, BrandInfo>(
                scopeFactory, brandSlots,
                (s, svc, brand) =>
                {
                    svc.Agent = services.Agent;
                    svc.Progress = services.Progress;
                    s.Geo = state.Geo;
                    s.SearchId = state.SearchId;
                    s.Brand = brand;
                    s.Result = brand;
                },
                services.Progress, SubWorkflowDispatcher.DefaultTimeout, context.CancellationToken,
                onCompleted: async (i, s) =>
                {
                    lock (gate)
                    {
                        brandSlots[i] = s.Result;
                        state.Events.AddRange(s.Events);
                        Merge();
                    }
                    await StreamAsync();
                },
                onAggregateEvent: e => { lock (gate) { state.Events.Add(e); } });

        var storeTask = storeSlots.Length == 0
            ? Task.CompletedTask
            : SubWorkflowDispatcher.RunManyAsync<StoreResearchWorkflow, StoreResearchState, StoreInfo>(
                scopeFactory, storeSlots,
                (s, svc, store) =>
                {
                    svc.Agent = services.Agent;
                    svc.Progress = services.Progress;
                    s.Geo = state.Geo;
                    s.SearchId = state.SearchId;
                    s.Store = store;
                    s.Result = store;
                },
                // Stores get the longer budget: the catalogue step needs more than the 30s default.
                services.Progress, SubWorkflowDispatcher.StoreResearchTimeout, context.CancellationToken,
                onCompleted: async (i, s) =>
                {
                    lock (gate)
                    {
                        storeSlots[i] = s.Result;
                        state.Events.AddRange(s.Events);
                        Merge();
                    }
                    await StreamAsync();
                },
                onAggregateEvent: e => { lock (gate) { state.Events.Add(e); } });

        var itemTask = itemSlots.Length == 0
            ? Task.CompletedTask
            : SubWorkflowDispatcher.RunManyAsync<ItemDeepDiveWorkflow, ItemDeepDiveState, ProductModel>(
                scopeFactory, itemSlots,
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
                services.Progress, SubWorkflowDispatcher.DefaultTimeout, context.CancellationToken,
                onCompleted: async (i, s) =>
                {
                    lock (gate)
                    {
                        itemSlots[i] = s.Result;
                        state.Events.AddRange(s.Events);
                        Merge();
                    }
                    await StreamAsync();
                },
                onAggregateEvent: e => { lock (gate) { state.Events.Add(e); } });

        await Task.WhenAll(brandTask, storeTask, itemTask);

        // Every entity has merged (per-entity callbacks) — one unconditional trailing push so the UI
        // holds the fully-enriched grid even before Aggregate/Completed lands.
        await services.TryPushPartialAsync(state);

        if (state.Products is { } enriched)
        {
            var enrichedBrands = enriched.Brands.Count(b => b.Reputation is not null);
            if (enrichedBrands > 0)
            {
                services.Report(SearchStep.BuildingProfiles, "Progress.Msg.EnrichedBrandsCount", enrichedBrands);
            }

            var verified = enriched.Stores.Count(s => s.Rating is not null);
            if (verified > 0)
            {
                services.Report(SearchStep.FindingStores, "Progress.Msg.VerifiedStoresCount", verified);
            }
        }
    }
}

/// <summary>Step 8 — assemble the final answer object from the summary, bundle and (enriched) products.</summary>
[Activity("Daleel", "Search", "Aggregate: assemble the final ranked answer + result count")]
public sealed class AggregateResultsActivity : CancellableActivity
{
    protected override ValueTask DoExecuteAsync(ActivityExecutionContext context)
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
public sealed class ModerateContentActivity : CancellableActivity
{
    protected override ValueTask DoExecuteAsync(ActivityExecutionContext context)
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
public sealed class CacheResultsActivity : CancellableActivity
{
    protected override ValueTask DoExecuteAsync(ActivityExecutionContext context)
    {
        var state = context.GetRequiredService<SearchPipelineState>();
        var services = context.GetRequiredService<SearchPipelineServices>();
        if (state.FromCache || state.Answer is null)
        {
            return ValueTask.CompletedTask;
        }

        // Skip an empty product search — a zero-result payload is too often a transient/upstream failure
        // rather than a true "nothing exists". A non-product "ask" answer (Products is null) is legitimate
        // and is still serialized.
        if (IsEmptyProductResult(state.Answer))
        {
            state.RecordEvent(EventCategory.Cache, "cache.skip-empty", "cache");
            return ValueTask.CompletedTask;
        }

        services.Report(SearchStep.ComparingPrices, "Progress.Msg.Saving");
        // Serialize the report so the runner persists it to SearchJob.ResultJson (history, sharing, the
        // detail pages). The whole-query result VAULT is no longer written (owner decision — see
        // CheckCacheActivity); that durable per-job record is NOT the vault.
        state.ResultJson = ResultSerialization.Serialize(state.Answer);
        state.ResultType = "ask";
        return ValueTask.CompletedTask;
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
public sealed class ReturnResultsActivity : CancellableActivity
{
    protected override ValueTask DoExecuteAsync(ActivityExecutionContext context)
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
