using System.Text.Json;
using Daleel.Agent;
using Daleel.Core.Caching;
using Daleel.Core.Models;
using Daleel.Core.Moderation;
using Daleel.Core.Observability;
using Daleel.Web.Data;
using Daleel.Web.Email;
using Daleel.Web.Events;
using Daleel.Web.Moderation;
using Daleel.Web.Pipeline;
using Daleel.Web.Pipeline.SubWorkflows;
using Daleel.Web.Services;
using Elsa.Workflows;
using Elsa.Workflows.Management;
using Elsa.Workflows.Models;
using Microsoft.EntityFrameworkCore;

namespace Daleel.Web.Conversation;

/// <summary>
/// Production <see cref="ISearchRunner"/> that drives the search through the Elsa
/// <see cref="SearchWorkflow"/> instead of calling <c>AgentService.AskAsync</c> directly. It still
/// owns the request-level concerns the workflow shouldn't: building the agent from server keys, cost
/// instrumentation/caps, and persisting the API-call + content-filter audit logs. The plan → gather →
/// analyze → enrich → aggregate → moderate → cache orchestration now lives in the workflow.
/// </summary>
public sealed class WorkflowSearchRunner : ISearchRunner
{
    /// <summary>TTL for both cache layers — a cached search stays valid for 30 days.</summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(30);

    /// <summary>
    /// Hard wall-clock ceiling on a single search run's SYNCHRONOUS phase (discover → scrape →
    /// extract → aggregate). The per-LLM-call (60s) and per-entity sub-workflow (30s) timeouts bound
    /// the individual awaits; this is the last-resort backstop that auto-cancels a run wedged for any
    /// other reason. NOT a result cap and NOT the whole story: enrichment (prices/images/stock/deep
    /// dives) continues AFTER this on the independent enrichment queue, which has no job deadline —
    /// so a tripped deadline salvages the products already extracted and the drain keeps filling
    /// them in. Raised from 10→15m (Kimi + wide uncapped fan-out legitimately need more headroom
    /// before the guillotine); tunable via <c>SEARCH_WORKFLOW_TIMEOUT_MINUTES</c>.
    /// </summary>
    private static readonly TimeSpan WorkflowTimeout = ResolveWorkflowTimeout();

    private static TimeSpan ResolveWorkflowTimeout()
    {
        var raw = Environment.GetEnvironmentVariable("SEARCH_WORKFLOW_TIMEOUT_MINUTES");
        var minutes = int.TryParse(raw, out var parsed) && parsed > 0 ? parsed : 15;
        return TimeSpan.FromMinutes(Math.Clamp(minutes, 2, 60));
    }

    private readonly IAgentFactory _agents;
    private readonly ISystemConfigService _config;
    private readonly IApiCallLogRepository _apiLog;
    private readonly IFilteredContentLogRepository _filteredLog;
    private readonly ICacheStore _cache;
    private readonly IEventStore _eventStore;
    private readonly ISystemEventLog _systemLog;
    private readonly ISearchEventSinkFactory _sinkFactory;
    private readonly ISearchEmailNotifier _emailNotifier;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IModerationPolicyProvider _moderationPolicy;
    private readonly IRelevancePolicyProvider _relevancePolicy;
    private readonly IHalalImageClassifier _imageClassifier;
    private readonly ILogger<WorkflowSearchRunner> _logger;
    private readonly IConversationBroadcaster? _broadcaster;

    public WorkflowSearchRunner(
        IAgentFactory agents, ISystemConfigService config, IApiCallLogRepository apiLog,
        IFilteredContentLogRepository filteredLog, ICacheStore cache, IEventStore eventStore,
        ISystemEventLog systemLog, ISearchEventSinkFactory sinkFactory,
        ISearchEmailNotifier emailNotifier, IServiceScopeFactory scopeFactory,
        IModerationPolicyProvider moderationPolicy, IRelevancePolicyProvider relevancePolicy,
        IHalalImageClassifier imageClassifier,
        ILogger<WorkflowSearchRunner> logger,
        IConversationBroadcaster? broadcaster = null)
    {
        _agents = agents;
        _config = config;
        _apiLog = apiLog;
        _filteredLog = filteredLog;
        _cache = cache;
        _moderationPolicy = moderationPolicy;
        _relevancePolicy = relevancePolicy;
        _imageClassifier = imageClassifier;
        _eventStore = eventStore;
        _systemLog = systemLog;
        _sinkFactory = sinkFactory;
        _emailNotifier = emailNotifier;
        _scopeFactory = scopeFactory;
        _logger = logger;
        // Optional so existing test wiring keeps working; production DI always supplies it.
        _broadcaster = broadcaster;
    }

    public async Task<SearchRunResult> RunAsync(SearchJob job, Action<string> progress, CancellationToken ct)
    {
        var language = string.IsNullOrWhiteSpace(job.Language) ? "en" : job.Language;
        var resultKey = CacheKey.ForResult(job.Query, job.Geo, language);

        // Per-job cost instrumentation: estimate spend from admin pricing, stream each call live.
        var estimator = await CostConfig.BuildEstimatorAsync(_config, ct).ConfigureAwait(false);

        // R1 — meter only, never cost-cancel a running search: a cost limit blocks NEW searches at
        // submission, never an in-flight one; actual spend is charged post-hoc.
        var collector = new JobApiCallCollector(
            line => _logger.LogInformation("Search job {JobId} API call · {Detail}", job.Id, line));

        // Ambient metering: DI-resolved components (vision identification, brand-catalogue
        // discovery/harvest, store-catalogue crawls) make paid calls on their own HTTP clients and
        // can't be wired by the AgentFactory — the ambient observer is how THEIR spend reaches the
        // same collector (usage dashboard, per-job estimate, cost cap) as everything else. Flows
        // through awaits and sub-workflow scopes; restored on dispose.
        using var ambient = AmbientApiObserver.Begin(collector, estimator);

        // Group every LLM call this search makes under one OpenRouter session id (`user` field).
        using var llmSession = AmbientLlmSession.Begin($"search-{job.Id}");

        // Live per-search event sink: pipeline steps emit semantic timeline events through the ambient
        // carrier (same AsyncLocal reach as the metering observer) so this search's events appear live.
        var eventSink = _sinkFactory.For(job.Id.ToString(), Anonymizer.HashUserId(job.UserId));
        using var events = AmbientSearchEvents.Begin(eventSink);

        // Admin feedback drives moderation: the active whitelist (undo decisions) and the
        // rating-tuned thresholds ride into the agent with every run. Best-effort by contract.
        var moderation = await _moderationPolicy.GetAsync(ct).ConfigureAwait(false);
        var relevance = await _relevancePolicy.GetAsync(job.Query, job.Geo, ct).ConfigureAwait(false);

        // Per-call-site pipeline models: each LLM step (planner, extraction, …) runs the model configured
        // for it (model.<site>), so steps can be cost-tuned independently at /admin/settings.
        var callSiteModels = await ResolveCallSiteModelsAsync(ct).ConfigureAwait(false);

        // Background jobs resolve keys from server env only (browser BYO keys aren't available here).
        var agent = _agents.Build(new AgentRequest
        {
            CallSiteModels = callSiteModels,
            Events = eventSink,
            Geo = job.Geo,
            Model = string.IsNullOrWhiteSpace(job.Model) ? null : job.Model,
            Language = language,
            Log = progress,
            ApiObserver = collector,
            CostEstimator = estimator,
            Cache = _cache,
            CacheTtl = CacheTtl,
            ModerationWhitelist = moderation.WhitelistKeys,
            ModerationCategories = moderation.Categories,
            HalalPolicy = moderation.Policy,
            RelevanceNegatives = relevance,
            ImageClassifier = _imageClassifier
        });

        // Captured from the run's state so the finally block can flush buffered cache/profile events
        // even when the workflow faults partway through.
        IReadOnlyList<PipelineEvent> bufferedEvents = Array.Empty<PipelineEvent>();

        try
        {
            // One scope per run so the per-run SearchPipelineState (scoped) is isolated; the Elsa
            // activities resolve that same instance from their execution context.
            using var scope = _scopeFactory.CreateScope();
            var state = scope.ServiceProvider.GetRequiredService<SearchPipelineState>();
            var services = scope.ServiceProvider.GetRequiredService<SearchPipelineServices>();
            state.Query = job.Query;
            state.Geo = job.Geo;
            state.Language = language;
            state.ResultKey = resultKey;
            state.CacheTtl = CacheTtl;
            state.SearchId = job.Id.ToString();
            state.StartedAt = DateTimeOffset.UtcNow;
            services.Agent = agent;
            services.Cache = _cache;
            services.Progress = progress;
            // Progressive results: activities push intermediate grids to the user's devices as soon as
            // data exists — the UI renders them while the run keeps going (never waits on the workflow).
            if (_broadcaster is { } bc)
            {
                services.PushPartial = (json, type) => bc.PartialAsync(job.UserId, job.Id, json, type);
            }
            bufferedEvents = state.Events;

            var runner = scope.ServiceProvider.GetRequiredService<IWorkflowRunner>();

            // Global workflow deadline: links off `ct` (user cancel / service shutdown still flow in) and
            // auto-cancels after WorkflowTimeout — a wall-clock liveness backstop, NOT a cost limit (R1:
            // cost never cancels a running search). Every activity sees this as context.CancellationToken,
            // so a wedged step is forcibly unwound instead of hanging forever.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(WorkflowTimeout);

            // The deadline token also cancels Elsa's own workflow-state commit, so a tripped deadline makes
            // RunAsync THROW OperationCanceledException instead of returning a faulted result — which used
            // to bypass the salvage below entirely and turn an almost-finished run (products already
            // extracted, only enrichment left) into a hard "no results" failure. Catch exactly that case
            // and fall through to the same salvage a faulted run gets; a genuine job cancellation (user
            // cancel / service shutdown, i.e. `ct`) still propagates untouched.
            RunWorkflowResult? run = null;
            try
            {
                run = await runner.RunAsync(new SearchWorkflow(), cancellationToken: deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger.LogError(
                    "Search workflow for job {JobId} was cancelled by the {Minutes}-minute deadline; salvaging any extracted products",
                    job.Id, WorkflowTimeout.TotalMinutes);
            }

            if (run is not null && deadline.IsCancellationRequested)
            {
                _logger.LogError(
                    "Search workflow for job {JobId} exceeded the {Minutes}-minute deadline and was cancelled",
                    job.Id, WorkflowTimeout.TotalMinutes);
            }

            // Persist the run as an Elsa workflow instance so the admin workflows page can list/replay it.
            // Persistence is optional and Postgres-only: when it isn't configured, IWorkflowInstanceManager
            // isn't registered and we simply skip — not an error. When it is, stamp a compact, serializable
            // run summary (query/outcome/timing) into the workflow state first (the live agent/cache/progress
            // live in SearchPipelineServices and never reach here, so the state is safe to persist) and save
            // before the sub-status check so faulted runs are tracked too. Best-effort: instance tracking
            // must never affect the search outcome.
            var instanceManager = scope.ServiceProvider.GetService<IWorkflowInstanceManager>();
            if (instanceManager is not null && run is not null)
            {
                try
                {
                    run.WorkflowState.Properties[WorkflowRunSummary.PropertyKey] =
                        JsonSerializer.Serialize(WorkflowRunSummary.From(state));
                    await instanceManager.SaveAsync(run.WorkflowState, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to persist workflow instance for search job {JobId}", job.Id);
                }
            }

            // A faulted/cancelled run reports Status == Finished but a non-Finished SubStatus, leaving
            // ResultJson empty or partial. The products are assembled up-front (step 4, ExtractProducts);
            // everything after — brand/store/item enrichment, the cache/serialize tail — is best-effort. So
            // rather than discard a result we already built and show a bare "no results", recover the
            // extracted products (un-enriched is fine) and log the incident so the underlying fault stays
            // diagnosable. Only a run that produced nothing usable is surfaced as a hard failure.
            if (run is null || run.WorkflowState.SubStatus != WorkflowSubStatus.Finished)
            {
                var subStatus = run?.WorkflowState.SubStatus.ToString() ?? "Cancelled";
                var reason = run is null
                    ? "workflow cancelled by the run deadline or cost cap before completing"
                    : string.Join("; ", run.WorkflowState.Incidents.Select(i => i.Message));
                _logger.LogWarning(
                    "Search workflow for job {JobId} ended non-finished ({SubStatus}): {Reason}",
                    job.Id, subStatus,
                    string.IsNullOrWhiteSpace(reason) ? "(no incident detail)" : reason);

                if (string.IsNullOrEmpty(state.ResultJson) && SalvageResultJson(state) is { } salvaged)
                {
                    state.ResultJson = salvaged;
                    state.ResultType = "ask";
                    state.ResultCount = state.Products?.ProductCount ?? 0;
                    _logger.LogWarning(
                        "Recovered {Count} product(s) from faulted search job {JobId} despite the incident above",
                        state.ResultCount, job.Id);
                }

                if (string.IsNullOrEmpty(state.ResultJson))
                {
                    throw new InvalidOperationException(
                        $"Search workflow did not finish (sub-status: {subStatus})" +
                        (string.IsNullOrWhiteSpace(reason) ? "." : $": {reason}"));
                }
            }

            // Side-effect guard: the user's Cancel is a durable flag (there is no per-job CTS), so a
            // cancel that landed mid-activity can reach this point with a freshly-built — or salvaged —
            // result. The job service's post-run backstop discards the result AFTER this method returns,
            // but the side effects below (the cache hit/miss record and the "results ready" email) would
            // already have fired for a user who cancelled. Re-read the flag and leave via the same
            // OperationCanceledException a cooperative cancel produces; ProcessAsync classifies it via
            // the durable flag and finalizes the job as Cancelled — no email, no bogus cache record.
            if (await IsCancelRequestedAsync(job.Id).ConfigureAwait(false))
            {
                throw new OperationCanceledException($"Search job {job.Id} was cancelled by the user.");
            }

            await RecordResultCacheAsync(job, state.FromCache ? "hit" : "miss", ct).ConfigureAwait(false);

            var providers = string.Join(",", collector.Calls
                .Select(c => c.Provider)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase));

            // Best-effort: email the user a summary now that the report is ready. The notifier swallows its
            // own failures (no provider, no address, opted out, send error), so it can never fail the search.
            await _emailNotifier.NotifySearchCompletedAsync(job, state.ResultJson, ct).ConfigureAwait(false);

            return new SearchRunResult(
                state.ResultJson,
                state.ResultType,
                state.FilteredCount,
                state.FilteredCategories,
                collector.Calls.Count,
                collector.TotalCost,
                state.ResultCount,
                providers,
                collector.TotalCredits)
            {
                // Carries the smart-cache verdict (set only on a hit) up to the worker, which decides
                // whether to launch a background partial re-enrichment for the missing pieces.
                CacheQuality = state.CacheQuality
            };
        }
        finally
        {
            await PersistAsync(job, collector, ct).ConfigureAwait(false);
            await PersistFilteredAsync(job, agent.ContentFilter.AuditDetails, ct).ConfigureAwait(false);
            await PersistEventsAsync(job, collector, bufferedEvents, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Flushes the run's pipeline events to the (optional) Postgres event store: every provider call
    /// (projected from the API-call collector) plus the buffered cache/profile events. No-op + cheap
    /// when the event store is the null/unconfigured one.
    /// </summary>
    private async Task PersistEventsAsync(
        SearchJob job, JobApiCallCollector collector, IReadOnlyList<PipelineEvent> buffered, CancellationToken ct)
    {
        if (!_eventStore.IsEnabled && !_systemLog.IsEnabled)
        {
            return;
        }

        var searchId = job.Id.ToString();
        // Provider-call firehose events (LLM/search/scrape/places) — NOT tee'd live (open decision:
        // provider-call cost stays end-of-run), so these still bridge to the timeline below.
        var providerEvents = collector.Calls.Select(c => PipelineEventFactory.FromApiCall(c, searchId)).ToList();
        var events = new List<PipelineEvent>(providerEvents.Count + buffered.Count);
        events.AddRange(providerEvents);
        events.AddRange(buffered);

        if (events.Count == 0)
        {
            return;
        }

        // The cost dashboard's provider-shaped firehose.
        await _eventStore.RecordBatchAsync(events, CancellationToken.None).ConfigureAwait(false);

        // Bridge the same run into the unified admin timeline, re-bucketed into user-facing categories and
        // tagged with the (hashed) owner so the timeline's per-user filter works for search activity.
        if (_systemLog.IsEnabled)
        {
            var userHash = Anonymizer.HashUserId(job.UserId);
            // ONLY the provider-call events — the buffered semantic events (cache/profile/item/…) are now
            // emitted LIVE via the AmbientSearchEvents tee in {Sub}SearchPipelineState.RecordEvent, so
            // re-projecting `buffered` here would DOUBLE-write them (SystemEvent has no idempotency key).
            var timeline = providerEvents
                .Select(e => SystemEventProjection.FromPipelineEvent(e, userHash))
                .ToList();
            if (timeline.Count > 0)
            {
                await _systemLog.PublishManyAsync(timeline, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Builds a serialized result from a run that faulted after products were extracted, so an enrichment
    /// or serialize-tail failure doesn't throw away the products the user searched for. Prefers the answer
    /// the aggregate step assembled; falls back to a fresh answer over the extracted products. If the full
    /// answer won't serialize (the heavy research bundle — scraped pages / social posts — is the likeliest
    /// culprit), it retries without that bundle. Returns null when there is nothing worth surfacing.
    /// <paramref name="includeResearch"/> = false skips the heavy bundle outright — used by the streamed
    /// PARTIAL pushes, which repeat every ~800ms and whose UI renders only the products.
    /// </summary>
    internal static string? SalvageResultJson(SearchPipelineState state, bool includeResearch = true)
    {
        var answer = state.Answer ?? new AgentAnswer
        {
            Question = state.Query,
            Geo = state.GeoProfile?.Key ?? state.Geo,
            QueryType = state.Strategy?.QueryType ?? Daleel.Core.Models.QueryType.General,
            Summary = state.Summary,
            Research = includeResearch ? state.Bundle ?? new ResearchBundle() : new ResearchBundle(),
            // The salvage/partial-push fallback must carry the search object too (a salvaged run persists this ResultJson).
            Products = state.Products.WithStrategy(state.Strategy),
            GeneratedAt = DateTimeOffset.UtcNow
        };

        if (answer.Products is not { } p ||
            !(p.HasListings || p.StoreCount > 0 || p.BrandCount > 0 || p.ReviewCount > 0))
        {
            return null;
        }

        try
        {
            return ResultSerialization.Serialize(answer);
        }
        catch
        {
            // Drop the research bundle and keep the products — a usable result beats none.
            try
            {
                return ResultSerialization.Serialize(answer with { Research = new ResearchBundle() });
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>Caps so a brand/store-heavy cached report can't fan re-research out into unbounded cost.</summary>
    private const int MaxReEnrichBrands = 15;
    private const int MaxReEnrichStores = 10;

    /// <summary>
    /// Smart-cache partial re-enrichment: a cache hit that scored below the full-quality bar was served
    /// already, and now we refill ONLY the gaps the <paramref name="report"/> flagged — thin products get
    /// the standard item deep-dive (itself gap-targeted), brands missing a logo/description are re-researched,
    /// and stores missing location/contact/maps data are re-verified. Each branch is skipped when its
    /// dimension is complete, so we never re-scrape what's already there. The refilled report overwrites the
    /// cache and is returned to stream to the UI; null when nothing could be improved.
    /// </summary>
    public async Task<SearchRunResult?> ReEnrichAsync(
        SearchJob job, SearchRunResult baseResult, CacheQualityReport report,
        Action<string> progress, CancellationToken ct)
    {
        if (!report.HasActionableGaps ||
            ResultSerialization.Deserialize<AgentAnswer>(baseResult.ResultJson) is not { Products: { } seed } answer)
        {
            return null;
        }

        // The repair pass is its own execution entry point (not under the job worker's scope), so it
        // stamps SearchJobId on its log lines itself — same per-search traceability as the base run.
        using var logScope = Logging.SearchLogScope.Begin(_logger, job.Id);

        var products = seed;
        var language = string.IsNullOrWhiteSpace(job.Language) ? "en" : job.Language;
        var resultKey = CacheKey.ForResult(job.Query, job.Geo, language);

        var estimator = await CostConfig.BuildEstimatorAsync(_config, ct).ConfigureAwait(false);
        // R1 — meter only, never cost-cancel ongoing re-enrichment.
        var collector = new JobApiCallCollector(
            line => _logger.LogInformation("Re-enrich job {JobId} API call · {Detail}", job.Id, line));

        // Ambient metering (see RunAsync): routes the vision/catalogue spend made on DI-resolved
        // components' own HTTP clients into this re-enrichment's collector.
        using var ambient = AmbientApiObserver.Begin(collector, estimator);
        using var llmSession = AmbientLlmSession.Begin($"search-{job.Id}");

        var reEnrichSink = _sinkFactory.For(job.Id.ToString(), Anonymizer.HashUserId(job.UserId));
        using var eventScope = AmbientSearchEvents.Begin(reEnrichSink);

        var callSiteModels = await ResolveCallSiteModelsAsync(ct).ConfigureAwait(false);
        var agent = _agents.Build(new AgentRequest
        {
            CallSiteModels = callSiteModels,
            Events = reEnrichSink,
            Geo = job.Geo, Model = string.IsNullOrWhiteSpace(job.Model) ? null : job.Model, Language = language,
            Log = progress, ApiObserver = collector, CostEstimator = estimator, Cache = _cache, CacheTtl = CacheTtl
        });

        var events = new List<PipelineEvent>();
        var changed = false;
        try
        {
            // 1) Products — only when models are thin (missing image/price/specs). The item deep-dive is
            //    itself gap-targeted: DB-first reuse, scrapes official specs only for thin items, fills prices.
            if (report.ThinProducts.Count > 0)
            {
                using var scope = _scopeFactory.CreateScope();
                var itemEnricher = scope.ServiceProvider.GetRequiredService<IItemEnrichmentService>();
                var itemResult = await itemEnricher
                    .EnrichAsync(agent, products, progress, job.Id.ToString(), ct).ConfigureAwait(false);
                events.AddRange(itemResult.Events);
                if (itemResult.Products is { } updated)
                {
                    products = updated;
                    changed = true;
                }
            }

            // 2) Brands — re-research only the ones missing a logo/description.
            if (report.DeficientBrands.Count > 0)
            {
                var (brands, brandEvents) = await ReResearchBrandsAsync(
                    agent, products, job, report, progress, ct).ConfigureAwait(false);
                events.AddRange(brandEvents);
                if (!brands.SequenceEqual(products.Brands))
                {
                    products = products with { Brands = brands };
                    changed = true;
                }
            }

            // 3) Stores — re-verify only the ones missing location/contact/Google Maps data.
            if (report.DeficientStores.Count > 0)
            {
                var (stores, storeEvents) = await ReResearchStoresAsync(
                    agent, products, job, report, progress, ct).ConfigureAwait(false);
                events.AddRange(storeEvents);
                if (!stores.SequenceEqual(products.Stores))
                {
                    products = products with { Stores = stores };
                    changed = true;
                }
            }
        }
        finally
        {
            // Re-enrichment cost + events show in the usage dashboard the same way a base run's do.
            await PersistAsync(job, collector, ct).ConfigureAwait(false);
            await PersistEventsAsync(job, collector, events, ct).ConfigureAwait(false);
        }

        if (!changed)
        {
            return null; // nothing improved — no UI update needed
        }

        var enrichedJson = ResultSerialization.Serialize(answer with { Products = products });

        // Overwrite the cached report with the refilled one (preserving the moderation stats) so the next
        // identical search replays the now-complete result instead of re-triggering enrichment.
        try
        {
            var cached = new CachedSearchResult(
                enrichedJson, baseResult.ResultType, baseResult.FilteredCount, baseResult.FilteredCategories);
            await _cache.SetAsync(resultKey, JsonSerializer.Serialize(cached), CacheTtl, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw; // cancellation propagates; SearchJobService treats it as a cancelled re-enrichment
        }
        catch
        {
            // best-effort: a cache write must never fail re-enrichment
        }

        // Clear CacheQuality on the streamed result: it's now refilled, so it must not loop into another pass.
        return baseResult with
        {
            ResultJson = enrichedJson,
            ResultCount = products.ProductCount,
            CacheQuality = null
        };
    }

    /// <summary>
    /// Re-runs the brand-research sub-workflow for just the brands the report flagged as deficient,
    /// matched by name, then folds the enriched brands back into the full brand list (order preserved).
    /// </summary>
    private async Task<(IReadOnlyList<BrandInfo> Brands, IReadOnlyList<PipelineEvent> Events)> ReResearchBrandsAsync(
        AgentService agent, ProductSearchResult products, SearchJob job, CacheQualityReport report,
        Action<string> progress, CancellationToken ct)
    {
        var wanted = new HashSet<string>(report.DeficientBrands, StringComparer.OrdinalIgnoreCase);
        var targets = products.Brands.Where(b => wanted.Contains(b.Name)).Take(MaxReEnrichBrands).ToList();
        if (targets.Count == 0)
        {
            return (products.Brands, Array.Empty<PipelineEvent>());
        }

        var results = await SubWorkflowDispatcher
            .RunManyAsync<BrandResearchWorkflow, BrandResearchState, BrandInfo>(
                _scopeFactory, targets,
                (s, svc, brand) =>
                {
                    svc.Agent = agent;
                    svc.Progress = progress;
                    s.Geo = job.Geo;
                    s.SearchId = job.Id.ToString();
                    s.Query = products.Query;
                    s.Brand = brand;
                    s.Result = brand;
                },
                progress, SubWorkflowDispatcher.DefaultTimeout, ct).ConfigureAwait(false);

        var events = new List<PipelineEvent>();
        var byName = new Dictionary<string, BrandInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in results)
        {
            events.AddRange(r.Events);
            if (!string.IsNullOrWhiteSpace(r.Result.Name))
            {
                byName[r.Result.Name] = r.Result;
            }
        }

        var merged = products.Brands
            .Select(b => byName.TryGetValue(b.Name, out var enriched) ? enriched : b)
            .ToList();
        return (merged, events);
    }

    /// <summary>
    /// Re-runs the store-research sub-workflow for just the stores the report flagged as deficient,
    /// matched by name, then folds the re-verified stores back into the full store list (order preserved).
    /// </summary>
    private async Task<(IReadOnlyList<StoreInfo> Stores, IReadOnlyList<PipelineEvent> Events)> ReResearchStoresAsync(
        AgentService agent, ProductSearchResult products, SearchJob job, CacheQualityReport report,
        Action<string> progress, CancellationToken ct)
    {
        var wanted = new HashSet<string>(report.DeficientStores, StringComparer.OrdinalIgnoreCase);
        var targets = products.Stores.Where(s => wanted.Contains(s.Name)).Take(MaxReEnrichStores).ToList();
        if (targets.Count == 0)
        {
            return (products.Stores, Array.Empty<PipelineEvent>());
        }

        var results = await SubWorkflowDispatcher
            .RunManyAsync<StoreResearchWorkflow, StoreResearchState, StoreInfo>(
                _scopeFactory, targets,
                (s, svc, store) =>
                {
                    svc.Agent = agent;
                    svc.Progress = progress;
                    s.Geo = job.Geo;
                    s.SearchId = job.Id.ToString();
                    s.Query = products.Query;
                    s.Store = store;
                    s.Result = store;
                },
                progress, SubWorkflowDispatcher.DefaultTimeout, ct).ConfigureAwait(false);

        var events = new List<PipelineEvent>();
        var byName = new Dictionary<string, StoreInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in results)
        {
            events.AddRange(r.Events);
            if (!string.IsNullOrWhiteSpace(r.Result.Name))
            {
                byName[r.Result.Name] = r.Result;
            }
        }

        var merged = products.Stores
            .Select(s => byName.TryGetValue(s.Name, out var enriched) ? enriched : s)
            .ToList();
        return (merged, events);
    }

    private async Task PersistFilteredAsync(
        SearchJob job, IReadOnlyList<FilterFinding> details, CancellationToken ct)
    {
        if (details.Count == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var rows = details.Select(d => FilteredContentLog.From(d, job.Query, job.Geo, now));

        try
        {
            await _filteredLog.AddBatchAsync(rows, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // best-effort: never let audit logging affect the search outcome
        }
    }

    /// <summary>
    /// Fresh durable read of the job's <c>CancelRequested</c> flag (written by the UI / sweep on a
    /// different DbContext). Best-effort like the activities' cooperative check: a failed read returns
    /// false and the worker's post-run backstop remains the hard guarantee that a cancelled job is
    /// finalized as cancelled.
    /// </summary>
    /// <summary>Snapshots each pipeline call-site's configured model (model.&lt;site&gt;) for this run; a
    /// missing/blank row leaves that call-site on its registry default (resolved in AgentFactory).</summary>
    private async Task<IReadOnlyDictionary<string, string>> ResolveCallSiteModelsAsync(CancellationToken ct)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var site in Daleel.Core.Llm.LlmCallSites.All)
        {
            var model = await _config.GetAsync(site.ConfigKey, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(model))
            {
                map[site.Key] = model!;
            }
        }

        return map;
    }

    private async Task<bool> IsCancelRequestedAsync(int jobId)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DaleelDbContext>();
            return await db.SearchJobs
                .Where(j => j.Id == jobId)
                .Select(j => j.CancelRequested)
                .FirstOrDefaultAsync(CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            return false; // best-effort — the job service's flag-reload backstop still runs after us
        }
    }

    private async Task PersistAsync(SearchJob job, JobApiCallCollector collector, CancellationToken ct)
    {
        var calls = collector.Calls;
        if (calls.Count == 0)
        {
            return;
        }

        var hashedUser = Anonymizer.HashUserId(job.UserId);
        var rows = calls.Select(c => new ApiCallLog
        {
            UserId = hashedUser, JobId = job.Id, Provider = c.Provider, Endpoint = c.Endpoint,
            RequestSummary = c.RequestSummary, ResponseSummary = c.ResponseSummary, ResponseTimeMs = c.ResponseTimeMs, ResponseBytes = c.ResponseBytes,
            Status = c.Status.ToString().ToLowerInvariant(), EstimatedCost = c.EstimatedCost,
            Model = c.Model, InputTokens = c.InputTokens, OutputTokens = c.OutputTokens, CreatedAt = c.Timestamp
        });

        try
        {
            await _apiLog.AddBatchAsync(rows, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // best-effort
        }
    }

    private async Task RecordResultCacheAsync(SearchJob job, string outcome, CancellationToken ct)
    {
        var row = new ApiCallLog
        {
            UserId = Anonymizer.HashUserId(job.UserId), JobId = job.Id, Provider = "cache",
            Endpoint = $"result/{outcome}", RequestSummary = RequestSummaries.Truncate(job.Query),
            Status = "success", EstimatedCost = 0m, CreatedAt = DateTimeOffset.UtcNow
        };

        try
        {
            await _apiLog.AddBatchAsync(new[] { row }, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // best-effort: cache telemetry must never affect the search outcome
        }
    }
}
