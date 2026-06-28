# 01 — Search Pipeline & Workflows

> Source-of-truth reference for how a Daleel search runs end to end: the Elsa orchestration,
> the per-entity sub-workflows, where every sub-workflow is triggered, where every price comes from,
> the exact external API calls, every LLM prompt, and the error-handling/salvage machinery.
> Everything here is drawn from the actual source with **file:line** references so you can trace
> exactly what happens at each step. When the code and this doc disagree, the code wins; fix the doc.
> (Line numbers reflect `main` at the time of writing — if a file shifts, search the method name.)

---

## 0. The shape in one paragraph

A user query becomes a queued `SearchJob`. The `SearchJobService` background worker picks it up,
builds a request-scoped `AgentService` from server keys, and hands the job to the
`WorkflowSearchRunner`. That runner creates a DI scope, seeds a `SearchPipelineState` +
`SearchPipelineServices`, and runs the **11-step Elsa `SearchWorkflow`** to completion in a single
in-process pass. Steps 5–7 fan out one **sub-workflow per brand / store / item** in bounded parallel,
each in its own DI scope. The runner reads the assembled `AgentAnswer` back off the state, caches it,
and returns it. After the result is on screen, the worker fires a **detached background enrichment**
(or cache re-enrichment) pass that fills specs and **pulls live prices** without blocking the queue.

```
SearchJob (queued)
   │
   ▼
SearchJobService.ProcessAsync          src/Daleel.Web/Conversation/SearchJobService.cs:53
   │   builds AgentService, streams progress over SignalR
   ▼
WorkflowSearchRunner.RunAsync          src/Daleel.Web/Conversation/WorkflowSearchRunner.cs:57
   │   DI scope, seeds state+services, cost cap, audit logs
   ▼
Elsa SearchWorkflow (1 → 11)           src/Daleel.Web/Pipeline/SearchWorkflow.cs:34
   ├─ 1  ParseQuery          plan
   ├─ 2  CheckCache          ←── quality-scored; short-circuits 2b–10 on a hit (FromCache=true)
   ├─ 2b AnalyzeMarket       category "thinking"
   ├─ 3  GatherSources       fan out to providers (prices first surface here)
   ├─ 4  ExtractProducts     LLM analyst + structured extraction (offers/prices per model)
   ├─ 5  DispatchBrandWorkflows   ─┐  SearchActivities.cs:313
   ├─ 6  DispatchStoreWorkflows    ├─ bounded-parallel per-entity sub-workflows  :365
   ├─ 7  DispatchItemWorkflows    ─┘  :416
   ├─ 8  AggregateResults     assemble AgentAnswer
   ├─ 9  ModerateContent      halal audit record
   ├─ 10 CacheResults         serialize + persist
   └─ 11 ReturnResults        finalize
   │
   ▼
SearchRunResult ──► result on screen ──► detached EnrichInBackground / ReEnrichInBackground
                                          (ItemEnrichmentService — live price harvest)
```

### Quick trigger map (the "where" answers)

| What | Triggered at | Then runs |
|------|--------------|-----------|
| One sub-workflow **per brand** | `DispatchBrandWorkflowsActivity.ExecuteAsync` → `SubWorkflowDispatcher.RunManyAsync` — [SearchActivities.cs:313](../../src/Daleel.Web/Pipeline/SearchActivities.cs) | `BrandResearchWorkflow` |
| One sub-workflow **per store/site** | `DispatchStoreWorkflowsActivity.ExecuteAsync` → `RunManyAsync` — [SearchActivities.cs:365](../../src/Daleel.Web/Pipeline/SearchActivities.cs) | `StoreResearchWorkflow` |
| One sub-workflow **per item/model** | `DispatchItemWorkflowsActivity.ExecuteAsync` → `RunManyAsync` — [SearchActivities.cs:416](../../src/Daleel.Web/Pipeline/SearchActivities.cs) | `ItemDeepDiveWorkflow` |
| The actual child run | `SubWorkflowDispatcher.RunChildAsync` → `IWorkflowRunner.RunAsync` — [SubWorkflowDispatcher.cs:129-130](../../src/Daleel.Web/Pipeline/SubWorkflows/SubWorkflowDispatcher.cs) | the child workflow in its own DI scope |
| **Per-store price scrape** | `ScrapePricesActivity.ExecuteAsync` → `ContextDevProvider.ExtractProductsAsync` — [StoreResearchActivities.cs:201](../../src/Daleel.Web/Pipeline/SubWorkflows/StoreResearchActivities.cs) | `POST /v1/brand/ai/products` |
| **Cross-store price fill** (background) | `ItemEnrichmentService.AttachCatalogPricesAsync` — [ItemEnrichmentService.cs:219](../../src/Daleel.Web/Pipeline/ItemEnrichmentService.cs) | Context.dev catalogues + browser fallback |
| **Cache min-quality check** | `CheckCacheActivity.ScoreQuality` → `CacheQualityValidator.Evaluate` — [SearchActivities.cs:140](../../src/Daleel.Web/Pipeline/SearchActivities.cs), [CacheQualityValidator.cs:114](../../src/Daleel.Web/Pipeline/CacheQualityValidator.cs) | serve / serve+enrich / miss |

---

## 1. Workflow overview & the deliberate design

`SearchWorkflow` ([SearchWorkflow.cs:30](../../src/Daleel.Web/Pipeline/SearchWorkflow.cs)) is a flat Elsa
`Sequence` of eleven unconditional `CodeActivity` nodes ([SearchWorkflow.cs:34-52](../../src/Daleel.Web/Pipeline/SearchWorkflow.cs)).
A **deliberate architectural tradeoff** is spelled out in the class doc-comment ([SearchWorkflow.cs:18-28](../../src/Daleel.Web/Pipeline/SearchWorkflow.cs)):

- The cache-hit short-circuit is **not** an Elsa `If` edge — every post-cache activity opens with a plain `if (state.FromCache) return;`.
- The brand/store/item fan-out is **not** a declarative `ForEach`/parallel activity — it is a `Task.WhenAll` inside the dispatch activities (`SubWorkflowDispatcher.RunManyAsync`).
- Elsa earns its keep as the **activity-registration + sequencing + per-step telemetry seam**, not as a declarative control-flow engine.

### Not resumable across suspend

`SearchPipelineState` lives in the **DI scope**, not Elsa's `WorkflowState`/`Variables`
([SearchPipelineState.cs:17-28](../../src/Daleel.Web/Pipeline/SearchPipelineState.cs)). A real
suspend/resume would build a fresh, blank state from a new scope and silently emit empty results. So
every activity is synchronous (no `Delay`/bookmarks). Keeping the state pure-data lets the runner
persist a **completed-run summary** (`WorkflowRunSummary`) for the admin workflows page — persistence
of *finished* runs, not mid-run suspend/resume.

### The eleven steps

| # | Activity (file:line) | Role | No-ops when |
|---|----------------------|------|-------------|
| 1 | `ParseQueryActivity` [:24](../../src/Daleel.Web/Pipeline/SearchActivities.cs) | Resolve market + LLM-plan into a bilingual strategy | — (always runs) |
| 2 | `CheckCacheActivity` [:60](../../src/Daleel.Web/Pipeline/SearchActivities.cs) | Score & replay a stored report; short-circuit the rest | cache disabled |
| 2b | `AnalyzeMarketActivity` [:170](../../src/Daleel.Web/Pipeline/SearchActivities.cs) | Up-front category "thinking" (type, stores, brands, schema) | `FromCache`, non-product |
| 3 | `GatherSourcesActivity` [:229](../../src/Daleel.Web/Pipeline/SearchActivities.cs) | Fan out the strategy across all providers | `FromCache` |
| 4 | `ExtractProductsActivity` [:251](../../src/Daleel.Web/Pipeline/SearchActivities.cs) | LLM analyst summary + structured product extraction | `FromCache` |
| 5 | `DispatchBrandWorkflowsActivity` [:293](../../src/Daleel.Web/Pipeline/SearchActivities.cs) | One `BrandResearchWorkflow` per brand (parallel) | `FromCache`, no brands |
| 6 | `DispatchStoreWorkflowsActivity` [:345](../../src/Daleel.Web/Pipeline/SearchActivities.cs) | One `StoreResearchWorkflow` per store (parallel) | `FromCache`, no stores |
| 7 | `DispatchItemWorkflowsActivity` [:397](../../src/Daleel.Web/Pipeline/SearchActivities.cs) | One `ItemDeepDiveWorkflow` per model (parallel) | `FromCache`, non-product, no models |
| 8 | `AggregateResultsActivity` [:446](../../src/Daleel.Web/Pipeline/SearchActivities.cs) | Assemble the final `AgentAnswer` + count | `FromCache` |
| 9 | `ModerateContentActivity` [:481](../../src/Daleel.Web/Pipeline/SearchActivities.cs) | Record the halal content-filter audit | `FromCache` |
| 10 | `CacheResultsActivity` [:504](../../src/Daleel.Web/Pipeline/SearchActivities.cs) | Serialize + persist the report under the result key | `FromCache`, no answer |
| 11 | `ReturnResultsActivity` [:543](../../src/Daleel.Web/Pipeline/SearchActivities.cs) | Terminal marker; stamp `CompletedAt` | — (always runs) |

### Progress stepper

Each activity calls `services.Report(SearchStep, key, args…)` ([SearchPipelineServices.cs:42](../../src/Daleel.Web/Pipeline/SearchPipelineServices.cs)),
encoding a structured signal the client localizes. `SearchStep` enum ([SearchProgressSignal.cs:11-20](../../src/Daleel.Web/Pipeline/SearchProgressSignal.cs)):
`Analyzing(0)`, `CheckingVault(1)`, `SearchingWeb(2)`, `ExtractingProducts(3)`, `BuildingProfiles(4)`,
`FindingStores(5)`, `ComparingPrices(6)`, `Done(7)`.

---

## 2. State management — two halves, split on purpose

### `SearchPipelineState` — pure data ([SearchPipelineState.cs:30](../../src/Daleel.Web/Pipeline/SearchPipelineState.cs))

Scoped, plain data only. **Inputs:** `Query`, `Geo` (`"jordan"`), `Language` (`"en"`), `ResultKey`,
`CacheTtl` (30d), `SearchId`. **Intermediate:** `GeoProfile`, `Strategy`, `Intelligence`, `Bundle`,
`Summary`, `Products`, `Answer`. **Outputs:** `FromCache`, `CacheQuality` (`[JsonIgnore]`), `ResultJson`,
`ResultType`, `FilteredCount`, `FilteredCategories`, `ResultCount`. Plus `Events` (buffered non-provider
events) and the computed `IsProductQuery` ([:92](../../src/Daleel.Web/Pipeline/SearchPipelineState.cs)).

### `SearchPipelineServices` — the live, non-serializable half ([SearchPipelineServices.cs:21](../../src/Daleel.Web/Pipeline/SearchPipelineServices.cs))

`Agent` (`AgentService`), `Cache` (`ICacheStore?`), `Progress` (`Action<string>?`); helpers `Log` and
`Report`. Each activity resolves both via `context.GetRequiredService<…>()`.

### Sub-workflow mirror

`SubWorkflowState` (base: `Geo`, `SearchId`, `Events` — [SubWorkflowState.cs:16](../../src/Daleel.Web/Pipeline/SubWorkflows/SubWorkflowState.cs))
+ `SubWorkflowServices` (`Agent`, `Progress`/`Log` — [SubWorkflowServices.cs:15](../../src/Daleel.Web/Pipeline/SubWorkflows/SubWorkflowServices.cs)).
Each child runs in its **own** DI scope → its own `DaleelDbContext` → the fan-out is concurrency-safe.

---

## 3. Each step in detail

### Step 1 — `ParseQueryActivity` ([SearchActivities.cs:24-56](../../src/Daleel.Web/Pipeline/SearchActivities.cs))

**Reads:** `Query`, `Geo`. **Writes:** `GeoProfile`, `Geo`, `Strategy`.
1. Market resolution: `GeoProfiles.DetectInText(Query)` ("AC in Dubai" → UAE) overrides the default; falls back to `GeoProfiles.ResolveOrDefault(Geo)` ([:33](../../src/Daleel.Web/Pipeline/SearchActivities.cs)).
2. `services.Agent.PlanAsync(PromptTemplates.PlanFreeform(query, geo), ct)` → `SearchStrategy` ([:36](../../src/Daleel.Web/Pipeline/SearchActivities.cs)).

### Step 2 — `CheckCacheActivity` (smart cache) ([SearchActivities.cs:60-161](../../src/Daleel.Web/Pipeline/SearchActivities.cs))

**This is where the minimum-data-quality cache check lives.**
1. `services.Cache is null` → return (caching off) ([:66](../../src/Daleel.Web/Pipeline/SearchActivities.cs)).
2. `Cache.GetAsync(ResultKey)` ([:74](../../src/Daleel.Web/Pipeline/SearchActivities.cs)); null / undeserializable → `cache.miss`, return.
3. **Score the report:** `ScoreQuality(validator, cached.ResultJson)` ([:92](../../src/Daleel.Web/Pipeline/SearchActivities.cs), helper at [:140](../../src/Daleel.Web/Pipeline/SearchActivities.cs)) → `ICacheQualityValidator.Evaluate` (§5.4). The `Decision`:
   - `Miss` → `cache.stale`, leave `FromCache=false`, run live ([:94-101](../../src/Daleel.Web/Pipeline/SearchActivities.cs)).
   - `ServeAndEnrich` → `FromCache=true`, `cache.partial`; runner later refills only the gaps ([:110-116](../../src/Daleel.Web/Pipeline/SearchActivities.cs)).
   - `ServeAsIs` → `FromCache=true`, `cache.hit` ([:117-121](../../src/Daleel.Web/Pipeline/SearchActivities.cs)).

Scoring uses `ResultSerialization.Deserialize<AgentAnswer>` so enums round-trip; a corrupt payload
defaults to `CacheQualityReport.Complete` ([:140-152](../../src/Daleel.Web/Pipeline/SearchActivities.cs)).

### Step 2b — `AnalyzeMarketActivity` ([SearchActivities.cs:170-225](../../src/Daleel.Web/Pipeline/SearchActivities.cs))

For a product query, **before sources are gathered**, calls `services.Agent.AnalyzeCategoryAsync(category, geo, ct)`
([:186](../../src/Daleel.Web/Pipeline/SearchActivities.cs)) → `SearchIntelligence` (product type, store
types, expected brands, comparison `ProductSchema`, price expectation, images-matter). Threaded into
extraction and onto the result.

### Step 3 — `GatherSourcesActivity` ([SearchActivities.cs:229-247](../../src/Daleel.Web/Pipeline/SearchActivities.cs))

`services.Agent.GatherAsync(strategy, geo, ct)` ([:241](../../src/Daleel.Web/Pipeline/SearchActivities.cs)) →
`ResearchBundle`. Deterministic parallel provider fan-out — **no LLM**. **Prices first surface here**
(shopping results → `Bundle.Prices`; see §6.1).

### Step 4 — `ExtractProductsActivity` ([SearchActivities.cs:251-283](../../src/Daleel.Web/Pipeline/SearchActivities.cs))

1. `AnalyzeAsync(query, geo, bundle, ct, system)` → `Summary` ([:264](../../src/Daleel.Web/Pipeline/SearchActivities.cs)); system is `ProductAnalystSystem` for product queries.
2. Product queries: `BuildProductSearchResultAsync(subject, geo, bundle, summary, ct, intelligence)` → `ProductSearchResult` ([:273](../../src/Daleel.Web/Pipeline/SearchActivities.cs)). **This is the up-front assembly of models + per-store `Offers` (prices)** — see §6.2. The salvage path depends on this having run.

### Steps 5–7 — Dispatch activities (the fan-out)

All three share the shape: guard → take first *N* → `RunManyAsync` → merge enriched + un-dispatched
rest → append child `Events` → write back `state.Products`.

| Step | Activity | Cap | `RunManyAsync` call | Sub-workflow |
|------|----------|-----|----------------------|--------------|
| 5 | `DispatchBrandWorkflowsActivity` | `MaxBrands=15` [:296](../../src/Daleel.Web/Pipeline/SearchActivities.cs) | [:313](../../src/Daleel.Web/Pipeline/SearchActivities.cs) | `BrandResearchWorkflow` |
| 6 | `DispatchStoreWorkflowsActivity` | `MaxStores=10` [:348](../../src/Daleel.Web/Pipeline/SearchActivities.cs) | [:365](../../src/Daleel.Web/Pipeline/SearchActivities.cs) | `StoreResearchWorkflow` |
| 7 | `DispatchItemWorkflowsActivity` | `MaxItems=20` [:400](../../src/Daleel.Web/Pipeline/SearchActivities.cs) | [:416](../../src/Daleel.Web/Pipeline/SearchActivities.cs) | `ItemDeepDiveWorkflow` |

Item dispatch threads `products.Schema` onto each child ([:429](../../src/Daleel.Web/Pipeline/SearchActivities.cs))
so the per-item spec merge can order against the category schema.

### Step 8 — `AggregateResultsActivity` ([SearchActivities.cs:446-477](../../src/Daleel.Web/Pipeline/SearchActivities.cs))

Builds `AgentAnswer { Question, Geo, QueryType, Summary, Research=Bundle, Products, GeneratedAt }`
([:458](../../src/Daleel.Web/Pipeline/SearchActivities.cs)); `ResultCount` from `Products?.ProductCount`.

### Step 9 — `ModerateContentActivity` ([SearchActivities.cs:481-500](../../src/Daleel.Web/Pipeline/SearchActivities.cs))

Halal filtering happens at the gather chokepoint; this step records the audit from
`services.Agent.ContentFilter.AuditLog` ([:493](../../src/Daleel.Web/Pipeline/SearchActivities.cs)).

### Step 10 — `CacheResultsActivity` ([SearchActivities.cs:504-539](../../src/Daleel.Web/Pipeline/SearchActivities.cs))

`ResultSerialization.Serialize(Answer)` ([:516](../../src/Daleel.Web/Pipeline/SearchActivities.cs)), then
`Cache.SetAsync(ResultKey, CachedSearchResult(...), CacheTtl)` ([:526](../../src/Daleel.Web/Pipeline/SearchActivities.cs)). Write failures swallowed; cancellation propagates.

### Step 11 — `ReturnResultsActivity` ([SearchActivities.cs:543-563](../../src/Daleel.Web/Pipeline/SearchActivities.cs))

Stamps `CompletedAt`, reports the final `Done` step.

---

## 4. Sub-workflows

The dispatcher gives each child its own DI scope + `DaleelDbContext`, a hard 30s timeout, and
best-effort semantics. See §4.4 for the dispatcher; §4.1–4.3 for each workflow's exact steps.

### 4.0 The fan-out seam — `SubWorkflowDispatcher` ([SubWorkflowDispatcher.cs:20](../../src/Daleel.Web/Pipeline/SubWorkflows/SubWorkflowDispatcher.cs))

```csharp
DefaultTimeout         = 30s;   // hard per-entity budget                              :23
MaxConcurrency         = 5;     // children at once (SemaphoreSlim gate)               :26
SystematicFailureRatio = 0.5;   // ≥50% faults ⇒ "systematic" → WARN event            :32
```

- `RunManyAsync<TWorkflow,TState,TItem>` ([:40](../../src/Daleel.Web/Pipeline/SubWorkflows/SubWorkflowDispatcher.cs)): fans `items` out ≤5 at a time, each seeded by the caller's lambda, bounded by the timeout. Returns finished states **in input order**.
- `RunChildAsync` ([:112](../../src/Daleel.Web/Pipeline/SubWorkflows/SubWorkflowDispatcher.cs)): fresh scope → resolve scoped `TState` + `SubWorkflowServices` → `seed(...)` → `IWorkflowRunner.RunAsync(new TWorkflow(), timeoutCts.Token)` ([:129-130](../../src/Daleel.Web/Pipeline/SubWorkflows/SubWorkflowDispatcher.cs)). A real outer cancel rethrows ([:132](../../src/Daleel.Web/Pipeline/SubWorkflows/SubWorkflowDispatcher.cs)); any other exception → `(state, faulted:true)`, entity flows through un-enriched ([:136-141](../../src/Daleel.Web/Pipeline/SubWorkflows/SubWorkflowDispatcher.cs)).
- **Systematic-failure detection** ([:78-88](../../src/Daleel.Web/Pipeline/SubWorkflows/SubWorkflowDispatcher.cs)): when `faults ≥ max(2, ceil(count×0.5))`, emit a `⚠️` progress line + a `subworkflow_failures`/`dispatcher` WARN event into the parent stream.

### 4.1 `BrandResearchWorkflow` — search the brand site, analyze reputation, save

**Workflow:** [BrandResearchWorkflow.cs:12](../../src/Daleel.Web/Pipeline/SubWorkflows/BrandResearchWorkflow.cs) ·
**Activities:** [BrandResearchActivities.cs](../../src/Daleel.Web/Pipeline/SubWorkflows/BrandResearchActivities.cs) ·
**State:** `BrandResearchState` ([BrandResearchState.cs:11](../../src/Daleel.Web/Pipeline/SubWorkflows/BrandResearchState.cs))

**What it searches for, where it saves, what LLM analysis happens:**

| # | Activity (file:line) | What it does — exact |
|---|----------------------|----------------------|
| 1 | `SearchBrandSiteActivity` [:18](../../src/Daleel.Web/Pipeline/SubWorkflows/BrandResearchActivities.cs) | DB-first: `IBrandRepository.GetByNameAsync(brand.Name)` ([:31](../../src/Daleel.Web/Pipeline/SubWorkflows/BrandResearchActivities.cs)). If a **fresh** profile (`!IsStale(now, Ttl)`) → `ResolvedFromCache=true`, reuse it, skip the paid research ([:29-34](../../src/Daleel.Web/Pipeline/SubWorkflows/BrandResearchActivities.cs)). |
| 2 | `ScrapeBrandCatalogActivity` [:47](../../src/Daleel.Web/Pipeline/SubWorkflows/BrandResearchActivities.cs) | If not cached and `IProfileResearcher.IsAvailable`: `researcher.ResearchBrandAsync(name, geo)` ([:65](../../src/Daleel.Web/Pipeline/SubWorkflows/BrandResearchActivities.cs)). Records `profile.brand`/`context.dev`. **This is the search + scrape + LLM step** — see "Inside `ResearchBrandAsync`" below. Degrades to the stale saved profile when no keys. |
| 3 | `SynthesizeBrandProfileActivity` [:86](../../src/Daleel.Web/Pipeline/SubWorkflows/BrandResearchActivities.cs) | Folds the saved 0–10 profile onto the UI's 1–5 `BrandReputation` via `ToReputation` ([:113](../../src/Daleel.Web/Pipeline/SubWorkflows/BrandResearchActivities.cs)): `Score = clamp(ReputationScore/2, 0, 5)`, `Pros`/`Complaints`/`Summary`. Backfills `Url` + the real DB id so the brand page routes by it ([:100-107](../../src/Daleel.Web/Pipeline/SubWorkflows/BrandResearchActivities.cs)). |
| 4 | `SaveBrandProfileActivity` [:125](../../src/Daleel.Web/Pipeline/SubWorkflows/BrandResearchActivities.cs) | **Persists** a freshly-researched profile: `IBrandRepository.UpsertAsync(Researched)` ([:139](../../src/Daleel.Web/Pipeline/SubWorkflows/BrandResearchActivities.cs)), stamps `LastRefreshed`, backfills the persisted DB id. Best-effort (`SafeUpsert`). Skips when served from cache. |
| 5 | `DownloadBrandImagesActivity` [:166](../../src/Daleel.Web/Pipeline/SubWorkflows/BrandResearchActivities.cs) | Collects the logo image URL(s). **No image R2 bucket is wired**, so it records `brand.images`/`r2` with `stored:false` ([:185](../../src/Daleel.Web/Pipeline/SubWorkflows/BrandResearchActivities.cs)) rather than silently doing nothing — a future R2 sink can pick them up. |

**Inside `ResearchBrandAsync`** — `ContextDevProfileResearcher` ([ContextDevProfileResearcher.cs:36](../../src/Daleel.Web/Profiles/ContextDevProfileResearcher.cs)):
1. `GatherBrandContextAsync` ([:155](../../src/Daleel.Web/Profiles/ContextDevProfileResearcher.cs)): guesses the domain (`GuessDomain` = slug + `.com`, [:215](../../src/Daleel.Web/Profiles/ContextDevProfileResearcher.cs)), calls Context.dev `GetBrandAsync(domain)` (`GET /v1/brand/retrieve`, [:163](../../src/Daleel.Web/Profiles/ContextDevProfileResearcher.cs)) for name/description/industry, then `ScrapeAsync(https://domain, Markdown)` (SSRF-guarded, capped 4000 chars, [:186-209](../../src/Daleel.Web/Profiles/ContextDevProfileResearcher.cs)).
2. **LLM analysis:** `new ProfileSynthesizer(llm).SynthesizeBrandAsync(brandName, context)` ([:53](../../src/Daleel.Web/Profiles/ContextDevProfileResearcher.cs)). The brand synth prompt + schema is in §7.5. Returns a `Brand` (countryOfOrigin, reputationScore 0–10, description, pros, cons, popularModels, priceRange, website).

### 4.2 `StoreResearchWorkflow` — scrape site, verify on Google Maps, persist, scrape prices

**Workflow:** [StoreResearchWorkflow.cs:12](../../src/Daleel.Web/Pipeline/SubWorkflows/StoreResearchWorkflow.cs) ·
**Activities:** [StoreResearchActivities.cs](../../src/Daleel.Web/Pipeline/SubWorkflows/StoreResearchActivities.cs) ·
**State:** `StoreResearchState` ([StoreResearchState.cs:11](../../src/Daleel.Web/Pipeline/SubWorkflows/StoreResearchState.cs))

**What data is pulled, how Google Maps is used, what gets persisted:**

| # | Activity (file:line) | What it does — exact |
|---|----------------------|----------------------|
| 1 | `ScrapeStoreSiteActivity` [:19](../../src/Daleel.Web/Pipeline/SubWorkflows/StoreResearchActivities.cs) | DB-first `IStoreRepository.GetByNameAsync` ([:29](../../src/Daleel.Web/Pipeline/SubWorkflows/StoreResearchActivities.cs)); fresh → reuse. Else `researcher.ResearchStoreAsync(name, geo)` ([:45](../../src/Daleel.Web/Pipeline/SubWorkflows/StoreResearchActivities.cs)) — scrape + LLM + **Google Maps verify** (below). Records `profile.store`. |
| 2 | `VerifyOnMapsActivity` [:74](../../src/Daleel.Web/Pipeline/SubWorkflows/StoreResearchActivities.cs) | Folds the Maps-verified fields onto the result: `Rating = Rating ?? GoogleRating ?? Rating`, `ReviewCount ?? GoogleReviewCount`, `Latitude/Longitude` ([:87-94](../../src/Daleel.Web/Pipeline/SubWorkflows/StoreResearchActivities.cs)). Records `store.verify`/`places`. |
| 3 | `ExtractContactInfoActivity` [:108](../../src/Daleel.Web/Pipeline/SubWorkflows/StoreResearchActivities.cs) | Folds `Address = Address ?? saved.Address ?? saved.Location` and `Phone` onto the result ([:119-123](../../src/Daleel.Web/Pipeline/SubWorkflows/StoreResearchActivities.cs)). |
| 4 | `SaveStoreProfileActivity` [:130](../../src/Daleel.Web/Pipeline/SubWorkflows/StoreResearchActivities.cs) | Finalizes `Url = Url ?? Website ?? GoogleMapsUrl` + DB id ([:139-145](../../src/Daleel.Web/Pipeline/SubWorkflows/StoreResearchActivities.cs)); **persists** a freshly-researched profile via `IStoreRepository.UpsertAsync` ([:156](../../src/Daleel.Web/Pipeline/SubWorkflows/StoreResearchActivities.cs)). Best-effort. |
| 5 | `ScrapePricesActivity` [:181](../../src/Daleel.Web/Pipeline/SubWorkflows/StoreResearchActivities.cs) | **The per-store price scrape.** Resolves `CONTEXT_DEV_API_KEY` + the store domain ([:192-197](../../src/Daleel.Web/Pipeline/SubWorkflows/StoreResearchActivities.cs)), then `new ContextDevProvider(key).ExtractProductsAsync(domain, maxProducts: 12)` (`POST /v1/brand/ai/products`, [:201/:222](../../src/Daleel.Web/Pipeline/SubWorkflows/StoreResearchActivities.cs)). Counts priced products into `PricedProducts`, records `store.prices`. |

**Inside `ResearchStoreAsync` + Google Maps** — `ContextDevProfileResearcher` ([:56](../../src/Daleel.Web/Profiles/ContextDevProfileResearcher.cs)):
1. `GatherStoreContextAsync` ([:177](../../src/Daleel.Web/Profiles/ContextDevProfileResearcher.cs)): scrape `https://{GuessDomain(storeName)}` (Markdown, 4000-char cap).
2. **LLM analysis:** `ProfileSynthesizer.SynthesizeStoreAsync` ([:73](../../src/Daleel.Web/Profiles/ContextDevProfileResearcher.cs)) → a `Store` (location, type, brandsCarried, rating 0–5, website, phone, email, address). Prompt in §7.5. Contact fallback: `ContactExtractor.FirstPhone/FirstEmail` from the scraped text ([:76-77](../../src/Daleel.Web/Profiles/ContextDevProfileResearcher.cs)).
3. **Google Maps cross-reference** — `VerifyOnPlacesAsync` ([:90-136](../../src/Daleel.Web/Profiles/ContextDevProfileResearcher.cs)):
   - `places.SearchStoresAsync(storeName, profile.Center, radius 15000m, primaryLanguage)` (`POST /v1/places:searchText`, [:101-103](../../src/Daleel.Web/Profiles/ContextDevProfileResearcher.cs)).
   - Pick a **name-matching** place (`NameMatches`, accent/space-insensitive contains, [:106/:139](../../src/Daleel.Web/Profiles/ContextDevProfileResearcher.cs)); fall back only if none matches.
   - Re-fetch full detail (`GetPlaceDetailsAsync`, the text-search mask omits hours/reviews, [:113](../../src/Daleel.Web/Profiles/ContextDevProfileResearcher.cs)).
   - **Stamp** `GooglePlaceId`, `GoogleMapsUrl`, `GoogleRating`, `GoogleReviewCount`, `Latitude/Longitude`, `OpeningHours` ([:115-124](../../src/Daleel.Web/Profiles/ContextDevProfileResearcher.cs)); backfill `Address`/`Phone`/`Website`/`Rating` only where empty ([:127-130](../../src/Daleel.Web/Profiles/ContextDevProfileResearcher.cs)). Best-effort — missing key / no confident match leaves the profile un-verified.

### 4.3 `ItemDeepDiveWorkflow` — the exact 9-step sequence

**Workflow:** [ItemDeepDiveWorkflow.cs:14](../../src/Daleel.Web/Pipeline/SubWorkflows/ItemDeepDiveWorkflow.cs) ·
**Activities:** [ItemDeepDiveActivities.cs](../../src/Daleel.Web/Pipeline/SubWorkflows/ItemDeepDiveActivities.cs) ·
**State:** `ItemDeepDiveState` ([ItemDeepDiveState.cs:13](../../src/Daleel.Web/Pipeline/SubWorkflows/ItemDeepDiveState.cs))

The workflow's declared order ([ItemDeepDiveWorkflow.cs:20-32](../../src/Daleel.Web/Pipeline/SubWorkflows/ItemDeepDiveWorkflow.cs))
is: **scrape pages → identify product → extract specs → save raw specs → merge/clean specs → save final
specs → compare prices → collect reviews → save item profile.**

| # | Activity (file:line) | What it does — exact |
|---|----------------------|----------------------|
| 1 | `ScrapeProductPagesActivity` [:22](../../src/Daleel.Web/Pipeline/SubWorkflows/ItemDeepDiveActivities.cs) | `Key = ProductProfile.KeyFor(Brand, Model, Name)` ([:38](../../src/Daleel.Web/Pipeline/SubWorkflows/ItemDeepDiveActivities.cs)). DB-first `IProductProfileRepository.GetByKeyAsync` ([:45](../../src/Daleel.Web/Pipeline/SubWorkflows/ItemDeepDiveActivities.cs)); fresh+non-empty → reuse, no network. Else a **thin** item (`Specs.Count < 3`, [:57](../../src/Daleel.Web/Pipeline/SubWorkflows/ItemDeepDiveActivities.cs)) with an offer URL (`ItemEnrichmentService.OfficialOrCheapestUrl`, [:56](../../src/Daleel.Web/Pipeline/SubWorkflows/ItemDeepDiveActivities.cs)) gets `agent.ReadPageAsync(url)` ([:63](../../src/Daleel.Web/Pipeline/SubWorkflows/ItemDeepDiveActivities.cs)); store ≤4000 chars of markdown into `Details`. |
| 2 | `IdentifyProductActivity` [:190](../../src/Daleel.Web/Pipeline/SubWorkflows/ItemDeepDiveActivities.cs) | `IProductIdentifier.IdentifyAsync(Model)` ([:200](../../src/Daleel.Web/Pipeline/SubWorkflows/ItemDeepDiveActivities.cs)) — text → cross-region → vision (§5). On a match sets `IdentifiedBrandModelId`, `MatchConfidence`, `MatchMethod`, `Category`. Records `item.identify`/(`openrouter` if vision, else `pipeline`). Best-effort. |
| 3 | `ExtractSpecsActivity` [:83](../../src/Daleel.Web/Pipeline/SubWorkflows/ItemDeepDiveActivities.cs) | Folds the scraped/reused `Details` markdown into `Result.Specs["details"]` ([:93](../../src/Daleel.Web/Pipeline/SubWorkflows/ItemDeepDiveActivities.cs)). |
| 4 | `SaveRawSpecsActivity` [:234](../../src/Daleel.Web/Pipeline/SubWorkflows/ItemDeepDiveActivities.cs) | Routes **each source's** raw data to its own R2 bucket and stages structured sources for the merge: store-listing specs → Specs bucket `raw/{brand}/{model}/store-listing.json` + `RawSpecsBySource.Add(SpecSource.Store(...))` ([:255-261](../../src/Daleel.Web/Pipeline/SubWorkflows/ItemDeepDiveActivities.cs)); identified brand-model specs → `raw/.../brand-site.json` + `SpecSource.Brand(...)` ([:264-276](../../src/Daleel.Web/Pipeline/SubWorkflows/ItemDeepDiveActivities.cs)); raw scraped markdown → Data bucket `site-data/{brand}/{model}/scraped-detail.json` ([:279-283](../../src/Daleel.Web/Pipeline/SubWorkflows/ItemDeepDiveActivities.cs)). The product image stays an external URL (no download, [:285-286](../../src/Daleel.Web/Pipeline/SubWorkflows/ItemDeepDiveActivities.cs)). DB stores only R2 URLs. |
| 5 | `MergeAndCleanSpecsActivity` [:307](../../src/Daleel.Web/Pipeline/SubWorkflows/ItemDeepDiveActivities.cs) | `ISpecMerger.Merge(RawSpecsBySource, Schema)` ([:319](../../src/Daleel.Web/Pipeline/SubWorkflows/ItemDeepDiveActivities.cs)) — dedupe / normalize units / resolve conflicts (brand wins) / order against schema (§5.3). Replaces `Result.Specs` with the canonical sheet ([:327](../../src/Daleel.Web/Pipeline/SubWorkflows/ItemDeepDiveActivities.cs)). Records `item.merge`. |
| 6 | `SaveFinalSpecsActivity` [:347](../../src/Daleel.Web/Pipeline/SubWorkflows/ItemDeepDiveActivities.cs) | Canonical sheet → R2 `final-specs/{brand}/{model}.json` (Specs bucket, [:372](../../src/Daleel.Web/Pipeline/SubWorkflows/ItemDeepDiveActivities.cs)); when identified and ≤8000 chars → `IBrandModelRepository.SaveFinalSpecsAsync(id, json, r2Url)` ([:379](../../src/Daleel.Web/Pipeline/SubWorkflows/ItemDeepDiveActivities.cs)). Records `item.finalspecs`. |
| 7 | `ComparePricesActivity` [:101](../../src/Daleel.Web/Pipeline/SubWorkflows/ItemDeepDiveActivities.cs) | In-memory: counts distinct stores across `Result.Offers` ([:108-112](../../src/Daleel.Web/Pipeline/SubWorkflows/ItemDeepDiveActivities.cs)). Records `item.compare`. No network. |
| 8 | `CollectReviewsActivity` [:128](../../src/Daleel.Web/Pipeline/SubWorkflows/ItemDeepDiveActivities.cs) | Records reviews/ratings already gathered (`Result.BrandReputation?.Social`, `ReviewSummary`, [:132-135](../../src/Daleel.Web/Pipeline/SubWorkflows/ItemDeepDiveActivities.cs)). No network. |
| 9 | `SaveItemProfileActivity` [:150](../../src/Daleel.Web/Pipeline/SubWorkflows/ItemDeepDiveActivities.cs) | Persists a freshly-scraped deep-dive: `IProductProfileRepository.UpsertAsync(ProductProfile{ NameKey=Key, Details, SourceUrl, … })` ([:163](../../src/Daleel.Web/Pipeline/SubWorkflows/ItemDeepDiveActivities.cs)). Skips when `ReusedFromCache`. Best-effort. |

> R2 writes go through the `ItemSpecPipeline` helpers (`SaveJson`/`SaveJsonBlob`/`SafeStoreJson`,
> [ItemDeepDiveActivities.cs:396-447](../../src/Daleel.Web/Pipeline/SubWorkflows/ItemDeepDiveActivities.cs)) —
> all best-effort: an R2 hiccup records nothing and never fails the search.

> **Note — image URLs.** The pipeline never downloads product images; it carries the source `ImageUrl`
> through (`ProductModel.ImageUrl` from extraction, the catalogue match in §6.3, or `BrandModel.ImageR2Urls`
> for vision). The UI renders external URLs directly. The only images copied to R2 are brand-model
> catalogue images harvested by `BrandCatalogSearcher` for vision matching.

---

## 5. Smart product identification & spec merge

### 5.1 `SmartProductIdentifier.IdentifyAsync` ([SmartProductIdentifier.cs:61-90](../../src/Daleel.Web/Identification/SmartProductIdentifier.cs))

Result: `ProductIdentification(int? BrandModelId, string? CanonicalModelName, string? Category, double Confidence, string Method)` ([:10-18](../../src/Daleel.Web/Identification/SmartProductIdentifier.cs)).

Three-stage cascade (needs `item.Brand`; looks up the brand row first, [:63-72](../../src/Daleel.Web/Identification/SmartProductIdentifier.cs)):
1. **Text match vs known models** — `_models.ListByBrandAsync(brand.Id)` → `TextMatch` ([:75-79](../../src/Daleel.Web/Identification/SmartProductIdentifier.cs)). `TextMatch` ([:93-123](../../src/Daleel.Web/Identification/SmartProductIdentifier.cs)) compares `BrandModel.Normalize(item.Model | item.Name)` against `ModelKey` then `RegionalAliases`. Hit → **confidence 1.0, method `"text"`**.
2. **Cross-region discovery + retry** — `SafeDiscoverAsync(brand)` (`IBrandCatalogSearcher`, Jordan→GCC→global) then `TextMatch` again ([:82-86](../../src/Daleel.Web/Identification/SmartProductIdentifier.cs)). Hit → **1.0, `"text"`**.
3. **Vision match** — `VisionMatchAsync` ([:89/:125-173](../../src/Daleel.Web/Identification/SmartProductIdentifier.cs)): needs `item.ImageUrl` + a configured matcher. Iterates `OrderForVision` candidates (≤`MaxVisionComparisons=8` *paid* comparisons, [:34/:139](../../src/Daleel.Web/Identification/SmartProductIdentifier.cs)), compares each via `ComparePairAsync` (cached comparisons are free, [:155-157](../../src/Daleel.Web/Identification/SmartProductIdentifier.cs)), early-accepts at `≥ EarlyAcceptConfidence 0.9` ([:165](../../src/Daleel.Web/Identification/SmartProductIdentifier.cs)), and returns the best only if `≥ VisionMatchCache.MatchThreshold` (0.75) → **method `"vision"`** ([:172](../../src/Daleel.Web/Identification/SmartProductIdentifier.cs)); else `None`.

### 5.2 `VisionMatcher` — the OpenRouter vision call ([VisionMatcher.cs:51](../../src/Daleel.Web/Identification/VisionMatcher.cs))

| Field | Value | Line |
|-------|-------|------|
| Endpoint | `POST https://openrouter.ai/api/v1/chat/completions` | [:56](../../src/Daleel.Web/Identification/VisionMatcher.cs) |
| Model | `anthropic/claude-sonnet-4` (`DefaultModel`, overridable) | [:54](../../src/Daleel.Web/Identification/VisionMatcher.cs) |
| Auth | `Authorization: Bearer {apiKey}` | [:125](../../src/Daleel.Web/Identification/VisionMatcher.cs) |
| Extra headers | `HTTP-Referer: …/Daleel`, `X-Title: Daleel` | [:126-127](../../src/Daleel.Web/Identification/VisionMatcher.cs) |
| Timeout | 2 minutes | [:82-85](../../src/Daleel.Web/Identification/VisionMatcher.cs) |

Body = system message + a user message whose `content` is `[ {type:text}, {type:image_url store},
{type:image_url brand} ]` (both full HTTP(S) URLs; non-HTTP short-circuits to `NoMatch`).
System prompt ([:60-65](../../src/Daleel.Web/Identification/VisionMatcher.cs)):

```
You are a product-identification expert comparing two product photographs. Decide whether both images show the SAME physical product (same brand, same model line, same variant). Ignore background, watermark, angle and lighting differences. Respond with ONLY a JSON object: {"same_product": boolean, "confidence": number between 0 and 1, "model_name": string}. Set model_name to the specific model you recognize, or null if unsure.
```

User prompt (with/without hint): `The second image is believed to be "{hint}". Is the first image the
same product?` / `Is the first image the same product as the second image?`. Output `VisionDto
{ same_product, confidence, model_name }`, confidence clamped to `[0,1]`; results memoized per
(store-image-hash, brand-model-id) via `VisionMatchCache`.

### 5.3 `SpecMerger.Merge` ([SpecMerger.cs:51-77](../../src/Daleel.Web/Identification/SpecMerger.cs))

Source priorities (`SpecSource`, [:11-29](../../src/Daleel.Web/Identification/SpecMerger.cs)):
`BrandPriority=100` (wins) · `StorePriority=50` · `ReviewPriority=10` (loses).

1. **Gather best candidates** ([:57-73](../../src/Daleel.Web/Identification/SpecMerger.cs)): iterate sources `OrderByDescending(Priority)`; for each `(key,value)` normalize key (`NormalizeKey`, regex `[^a-z0-9]+`→`_`, [:117](../../src/Daleel.Web/Identification/SpecMerger.cs)) and value (`UnitNormalizer.Normalize`, [:62](../../src/Daleel.Web/Identification/SpecMerger.cs)); on a key clash **keep the higher-priority value**, ties keep first-seen ([:68-70](../../src/Daleel.Web/Identification/SpecMerger.cs)).
2. **Apply schema** when present (`ApplySchema`, [:80-107](../../src/Daleel.Web/Identification/SpecMerger.cs)): re-key recognized specs to the schema's `Key` (matched by `Key` or normalized `Label`, [:89-96](../../src/Daleel.Web/Identification/SpecMerger.cs)), emit schema-first, unclaimed specs alphabetically after ([:101-104](../../src/Daleel.Web/Identification/SpecMerger.cs)). No schema → plain alphabetical (`Order`, [:109](../../src/Daleel.Web/Identification/SpecMerger.cs)).

`UnitNormalizer` canonicalizes length to `"{inches} inches / {cm} cm"` (1in=2.54cm); other specs are
whitespace-collapsed only.

### 5.4 `CacheQualityValidator` — the minimum-data-quality scorer ([CacheQualityValidator.cs:103](../../src/Daleel.Web/Pipeline/CacheQualityValidator.cs))

The deterministic scorer behind the §3 step-2 decision. `Evaluate(answer)` ([:114](../../src/Daleel.Web/Pipeline/CacheQualityValidator.cs))
blends three dimensions, counting a dimension only when entities of that kind exist:

| Dimension | Weight | Per-entity score (file:line) |
|-----------|--------|------------------------------|
| Products | 50 | image 0.30 + price 0.30 + specs 0.25 + model 0.10 + SKU 0.05 (specs ≥3→1.0, ≥1→0.5) — `ScoreProducts` [:153](../../src/Daleel.Web/Pipeline/CacheQualityValidator.cs) |
| Brands | 25 | logo 0.6 + description 0.4 — `ScoreBrands` [:203](../../src/Daleel.Web/Pipeline/CacheQualityValidator.cs) |
| Stores | 25 | location 0.5 + contact 0.3 + Maps data 0.2 — `ScoreStores` [:242](../../src/Daleel.Web/Pipeline/CacheQualityValidator.cs) |

`Score = round(100 × weightedSum / weightTotal)` ([:144](../../src/Daleel.Web/Pipeline/CacheQualityValidator.cs)).
Non-product / empty answers → `Complete` (100). Decision thresholds ([:74-78](../../src/Daleel.Web/Pipeline/CacheQualityValidator.cs)):
`≥80` `ServeAsIs`; `<30` `Miss`; in between `ServeAndEnrich` **iff** actionable targets exist
(`ThinProducts`/`DeficientBrands`/`DeficientStores`), else `ServeAsIs`. The report carries the exact
refill targets + a `CacheGap` flags set the re-enrichment branches on.

---

## 6. Price aggregation — where every price comes from

Prices enter the result from **four** places. Tracing them top to bottom:

### 6.1 Gather-time shopping prices (during step 3)

`GatherSourcesActivity` → `AgentService.GatherAsync` runs `GoogleShoppingProvider.SearchPricesAsync`
([GoogleShoppingProvider.cs:37](../../src/Daleel.Search/Providers/GoogleShoppingProvider.cs)) and
`OpenSooqProvider` over the strategy's shopping queries; the extracted `PricePoint`s land on
`ResearchBundle.Prices`. These are raw market prices, not yet attached to a specific model.

### 6.2 Extraction-time per-model offers (during step 4)

`AgentService.BuildProductSearchResultAsync` (the LLM `ExtractProducts` call, §7.4) emits one entry per
model with an `offers[]` array (`source`, `price`, `currency`, `url`, `condition`). This is the
**primary** price source: each `ProductModel.Offers` is a list of `PriceOffer`. `ComparePricesActivity`
(item step 7) only **counts** these distinct store offers — it does not fetch new prices.

### 6.3 Per-store catalogue scrape (during step 6)

`ScrapePricesActivity` ([StoreResearchActivities.cs:181](../../src/Daleel.Web/Pipeline/SubWorkflows/StoreResearchActivities.cs))
calls `ContextDevProvider.ExtractProductsAsync(domain, maxProducts: 12)` (`POST /v1/brand/ai/products`)
for the verified store's own site and counts the priced products into `StoreResearchState.PricedProducts`.

### 6.4 Background cross-store price fill — `ItemEnrichmentService` (after first paint)

**This is the class+method that pulls prices from all sources for items still missing one.**
`ItemEnrichmentService.EnrichAsync` ([ItemEnrichmentService.cs:90](../../src/Daleel.Web/Pipeline/ItemEnrichmentService.cs))
runs detached (via `SearchJobService.EnrichInBackgroundAsync`) in five phases over the top
`MaxItems=20` models ([:36/:106](../../src/Daleel.Web/Pipeline/ItemEnrichmentService.cs)):

| Phase | Method / line | What |
|-------|---------------|------|
| 1 | `EnrichAsync` loop [:109-141](../../src/Daleel.Web/Pipeline/ItemEnrichmentService.cs) | Price-comparison + DB-first reuse (sequential — scoped DbContext). Counts store offers, records `item.compare`; queues **thin** items (`Specs.Count < 3`) with an offer URL for scraping (≤`MaxNewScrapes=8`). |
| 2 | scrape `Task.WhenAll` [:143-154](../../src/Daleel.Web/Pipeline/ItemEnrichmentService.cs) | Scrape the misses concurrently — `agent.ReadPageAsync(url)`, ≤4000 chars. |
| 3 | persist [:156-174](../../src/Daleel.Web/Pipeline/ItemEnrichmentService.cs) | Upsert each fresh deep-dive to `ProductProfile` (records `item.deepdive`/`context.dev`). |
| 4 | **`AttachCatalogPricesAsync`** [:189-344](../../src/Daleel.Web/Pipeline/ItemEnrichmentService.cs) | **The cross-store price fill** (below). |
| 5 | `HarvestBrandCatalogsAsync` [:193-385](../../src/Daleel.Web/Pipeline/ItemEnrichmentService.cs) | Side effect: harvest top `MaxBrandCatalogs=2` brands' sites into the `BrandModel` DB via `IBrandCatalogService.HarvestAsync`. |

**`AttachCatalogPricesAsync`** ([:219](../../src/Daleel.Web/Pipeline/ItemEnrichmentService.cs)) — only for
models where `!HasPrice(m)` ([:223/:505](../../src/Daleel.Web/Pipeline/ItemEnrichmentService.cs)), over the
top `MaxCatalogSites=2` store domains ([:228-233](../../src/Daleel.Web/Pipeline/ItemEnrichmentService.cs)),
under a hard `CatalogTimeoutMs=30s (+8s)` cap ([:241-242](../../src/Daleel.Web/Pipeline/ItemEnrichmentService.cs)):

1. **Primary — Context.dev structured catalogue:** `SafeCatalog → ContextDevProvider.ExtractProductsAsync(domain, maxProducts:12, timeoutMs:30s)` ([:256-264/:507-512](../../src/Daleel.Web/Pipeline/ItemEnrichmentService.cs)); keep priced products into `pool`. Records `catalog.products`/`context.dev`.
2. **Secondary — browser fallback** (`HarvestViaBrowserAsync`, [:270/:395-427](../../src/Daleel.Web/Pipeline/ItemEnrichmentService.cs)) for domains Context.dev returned nothing priced for: render the store's **on-site search page** (`/search?q=…`, [:435](../../src/Daleel.Web/Pipeline/ItemEnrichmentService.cs)) through `agent.ReadPageAsync` (scrape router → Cloudflare Browser), then `PriceParser.Extract` the markdown ([:417](../../src/Daleel.Web/Pipeline/ItemEnrichmentService.cs)). Records `catalog.browser`.
3. **Attribution** ([:280-334](../../src/Daleel.Web/Pipeline/ItemEnrichmentService.cs)): for each unpriced model, `BestCatalogMatch` ([:526](../../src/Daleel.Web/Pipeline/ItemEnrichmentService.cs)) then `BestBrowserMatch` ([:447](../../src/Daleel.Web/Pipeline/ItemEnrichmentService.cs)) by **shared significant tokens** — gated by `IsStrongMatch` (≥`MinMatchTokens=2` and ≥`MinMatchRatio=0.8` of the shorter token set, [:558](../../src/Daleel.Web/Pipeline/ItemEnrichmentService.cs)) so an incidental brand word never mis-attributes a price. The match becomes a new `PriceOffer` appended to `m.Offers`; a catalogue image fills `ImageUrl` when missing ([:328-331](../../src/Daleel.Web/Pipeline/ItemEnrichmentService.cs)).
4. **Persist history** ([:312-322/:478-500](../../src/Daleel.Web/Pipeline/ItemEnrichmentService.cs)): each match is written to the `ScrapedPrice` history (`IScrapedPriceRepository.AddRangeAsync`) tagged `context.dev` or `cloudflare-browser` — the price-history the comparison view reads.

> Which URL gets scraped for an item's specs/price: `OfficialOrCheapestUrl(m)`
> ([:615-618](../../src/Daleel.Web/Pipeline/ItemEnrichmentService.cs)) — brand page (`ResultType.BrandPage`)
> first, then the lowest-priced offer, then any offer.

The `ReEnrichAsync` path (smart-cache partial refill) reuses the **same** `IItemEnrichmentService` for
its thin-products branch ([WorkflowSearchRunner.cs:382-391](../../src/Daleel.Web/Conversation/WorkflowSearchRunner.cs)).

---

## 7. Exact external API calls

All providers live in `src/Daleel.Search/Providers/`. Each retries up to **2** times. Keys come from
server environment variables.

### 7.1 SerpAPI — `SerpApiProvider` ([SerpApiProvider.cs:21](../../src/Daleel.Search/Providers/SerpApiProvider.cs))

`GET https://serpapi.com/search.json`. `engine` per `SearchKind` (`google`, `google_shopping`,
`google_maps`, `google_news`, [:56-62](../../src/Daleel.Search/Providers/SerpApiProvider.cs)). Params
([BuildUrl :148-174](../../src/Daleel.Search/Providers/SerpApiProvider.cs)): `engine`, `q`, `num`,
`start` (0,10,20…), `safe=active`, `gl`, `hl`, `location`, `api_key` (`SERPAPI_KEY`). Pages ≤`MaxPages=10`,
dedup by URL/title+position. Parses `organic_results` / `shopping_results` (+price) / `local_results`.

### 7.2 Google Shopping — `GoogleShoppingProvider`

Adapter over an `ISearchProvider` (SerpAPI) with `SearchKind.Shopping`; maps to `PricePoint` /
`StoreResult`. No direct external call.

### 7.3 Google Places (New) — `GooglePlacesProvider` ([GooglePlacesProvider.cs:22](../../src/Daleel.Search/Providers/GooglePlacesProvider.cs))

Base `https://places.googleapis.com`; auth `X-Goog-Api-Key` (`GOOGLE_PLACES_API_KEY`) + per-request
`X-Goog-FieldMask`.

| Endpoint | Method | Purpose |
|----------|--------|---------|
| `/v1/places:searchText` [:75](../../src/Daleel.Search/Providers/GooglePlacesProvider.cs) | POST | Text search (`textQuery`, `languageCode`, optional `locationBias.circle`) |
| `/v1/places:searchNearby` [:101](../../src/Daleel.Search/Providers/GooglePlacesProvider.cs) | POST | Nearby (`includedTypes`, `maxResultCount:20`, `locationRestriction.circle`) |
| `/v1/places/{placeId}` [:112](../../src/Daleel.Search/Providers/GooglePlacesProvider.cs) | GET | Detail (full field mask: hours + reviews) |

Field masks include `id, displayName, formattedAddress, internationalPhoneNumber, websiteUri, location,
googleMapsUri, rating, userRatingCount, priceLevel, regularOpeningHours.weekdayDescriptions, reviews`.
This is the API behind store Google-Maps verification (§4.2).

### 7.4 Context.dev — `ContextDevProvider` ([ContextDevProvider.cs:47](../../src/Daleel.Search/Providers/ContextDevProvider.cs))

Base `https://api.context.dev`; auth `Authorization: Bearer {CONTEXT_DEV_API_KEY}`.

| Endpoint | Method | Used by |
|----------|--------|---------|
| `/v1/web/scrape/{markdown\|html}?url=…` [:81](../../src/Daleel.Search/Providers/ContextDevProvider.cs) | GET | page scrape (brand/store research, item deep-dive) |
| `/v1/web/extract` [:117](../../src/Daleel.Search/Providers/ContextDevProvider.cs) | POST | schema extraction |
| `/v1/brand/retrieve?domain=…` [:136](../../src/Daleel.Search/Providers/ContextDevProvider.cs) | GET | brand profile (name/desc/industry/logo) |
| `/v1/web/crawl` [:181](../../src/Daleel.Search/Providers/ContextDevProvider.cs) | POST | multi-page crawl |
| `/v1/brand/ai/products` [:220](../../src/Daleel.Search/Providers/ContextDevProvider.cs) | POST | **catalogue + prices** (`{domain, maxProducts, timeoutMS}`) — store price scrape (§6.3) + cross-store fill (§6.4) |

### 7.5 Bing / OpenSooq

- **Bing** ([BingProvider.cs](../../src/Daleel.Search/Providers/BingProvider.cs)): `GET https://api.bing.microsoft.com/v7.0/search` (`/news/search`), params `q`/`count`/`mkt`, header `Ocp-Apim-Subscription-Key` (`BING_SEARCH_KEY`).
- **OpenSooq** ([OpenSooqProvider.cs](../../src/Daleel.Search/Providers/OpenSooqProvider.cs)): no direct call — builds `https://{cc}.opensooq.com/en/search?term=…` and routes through a delegated `IScrapeProvider`, regex-extracting listings + prices.

---

## 8. LLM prompts

All search prompts live in [PromptTemplates.cs](../../src/Daleel.Agent/PromptTemplates.cs); every system
prompt ends with **`HalalGuard`** ([:30-35](../../src/Daleel.Agent/PromptTemplates.cs)). Profile-synthesis
prompts live in [ProfileSynthesizer.cs](../../src/Daleel.Web/Profiles/ProfileSynthesizer.cs).

### 8.1 Planning — `PlanFreeform` ([PromptTemplates.cs:85](../../src/Daleel.Agent/PromptTemplates.cs))

System `PlannerSystem` ([:38](../../src/Daleel.Agent/PromptTemplates.cs)): *"You are Daleel, an Arabic-first
market-intelligence research planner… You ALWAYS reply with a single JSON object only."* User = market
context + question + `StrategySchema` ([:69](../../src/Daleel.Agent/PromptTemplates.cs)):

```json
{ "queryType": "ProductResearch|BrandLookup|StoreFinder|DealHunter|OpinionAggregation|Comparison|General",
  "subject": "...", "webQueries": [...], "shoppingQueries": [...], "socialQueries": [...],
  "placesQueries": [...], "urlsToRead": [...], "reasoning": "..." }
```

### 8.2 Category intelligence — `CategoryIntelligence` ([PromptTemplates.cs:395](../../src/Daleel.Agent/PromptTemplates.cs))

System `CategoryIntelligenceSystem` ([:379](../../src/Daleel.Agent/PromptTemplates.cs)). Returns
`{ productType, relevantStoreTypes[], expectedBrands[], priceExpectation, imagesMatter,
specs[{key,label,unit,higherIsBetter,importance}], reasoning }` (4–8 specs, 2–3 marked "key").

### 8.3 Analyst summary — `Analyze` ([PromptTemplates.cs:441](../../src/Daleel.Agent/PromptTemplates.cs))

System `ProductAnalystSystem` ([:183](../../src/Daleel.Agent/PromptTemplates.cs)) for product queries
(aggregate one entry per model, local-only, budget/mid/premium tiers, assess brand reputation + local
service), else `AnalystSystem` ([:46](../../src/Daleel.Agent/PromptTemplates.cs)).

### 8.4 Product extraction (two LLM calls in `BuildProductSearchResultAsync`)

**(a)** `ExtractProducts` ([PromptTemplates.cs:310](../../src/Daleel.Agent/PromptTemplates.cs)) / system
`ProductExtractionSystem` ([:204](../../src/Daleel.Agent/PromptTemplates.cs)): extract ONLY evidenced
products, ONE entry per distinct model with an `offers[]` array (this is where per-model prices come
from, §6.2), prices as numbers, original-language names. When a schema is present, a "PRIORITISE these
fields (exact keys)" block is injected. Output:

```json
{ "products": [ { "name","brand","model","imageUrl","specs":{},
  "offers":[{"source","price","currency","url","condition"}],
  "pros":[],"cons":[],"summary" } ] }
```

**(b)** `BrandReputations` ([PromptTemplates.cs:237](../../src/Daleel.Agent/PromptTemplates.cs)) / system
`BrandReputationSystem` ([:225](../../src/Daleel.Agent/PromptTemplates.cs)): per-brand in-market
reliability, local service/warranty, plus quoted real user reviews (especially Arabic). Output
`{ brands:[{ brand, score(1–5), pros, complaints, hasLocalService, serviceNote, warranty, summary,
reviews:[{quote,originalText,source,url,sentiment,date,language}] }] }`.

### 8.5 Profile synthesis (brand/store sub-workflows) — `ProfileSynthesizer`

**Brand** — `SynthesizeBrandAsync` ([ProfileSynthesizer.cs:30](../../src/Daleel.Web/Profiles/ProfileSynthesizer.cs)),
system `BrandSystem` ([:15](../../src/Daleel.Web/Profiles/ProfileSynthesizer.cs)): *"You are a market
analyst… Respond with ONLY a JSON object… Use null/empty when unsure; never invent specific facts…"*
User schema ([:35-37](../../src/Daleel.Web/Profiles/ProfileSynthesizer.cs)):

```
{ "countryOfOrigin": string, "reputationScore": number (0-10), "description": string,
  "pros": string[], "cons": string[], "popularModels": string[], "priceRange": string, "website": string }
```

**Store** — `SynthesizeStoreAsync` ([ProfileSynthesizer.cs:44](../../src/Daleel.Web/Profiles/ProfileSynthesizer.cs)),
system `StoreSystem` ([:21](../../src/Daleel.Web/Profiles/ProfileSynthesizer.cs)). User schema
([:49-52](../../src/Daleel.Web/Profiles/ProfileSynthesizer.cs)):

```
{ "location": string, "type": string, "brandsCarried": string[], "rating": number (0-5),
  "website": string, "phone": string, "email": string, "address": string }
"Extract phone/email/address only if explicitly present in the context; never invent them."
```

### 8.6 Vision identification

`VisionMatcher` (§5.2) — the only vision prompt; deterministic `SpecMerger` (§5.3) has **no** LLM call.

---

## 9. Error handling, salvage, retries & timeouts

**Everything after product extraction (step 4) is best-effort** — a later fault never discards the
products the user searched for.

- **Cancellation discipline:** every `catch` does `catch (OperationCanceledException) { throw; }` first, then swallows. A cap-trip / user cancel stops the job; ordinary failures degrade.
- **Cost cap:** `WorkflowSearchRunner` wires a `JobApiCallCollector` to a `capTrip` CTS ([WorkflowSearchRunner.cs:66-70](../../src/Daleel.Web/Conversation/WorkflowSearchRunner.cs)); exceeding `CostCaps.MaxPerJob` ([CostConfig.cs:7](../../src/Daleel.Web/Data/CostConfig.cs)) cancels it and stops the whole workflow (which runs under `capTrip.Token`, [:108](../../src/Daleel.Web/Conversation/WorkflowSearchRunner.cs)).
- **Per-entity timeouts:** each sub-workflow child under `CancelAfter(30s)` (`SubWorkflowDispatcher.DefaultTimeout`); background enrichment under `90s` (`SearchJobService.EnrichTimeout`, [:189](../../src/Daleel.Web/Conversation/SearchJobService.cs)).
- **Faulted-run salvage** ([WorkflowSearchRunner.cs:138-162](../../src/Daleel.Web/Conversation/WorkflowSearchRunner.cs)): a non-`Finished` SubStatus logs the incident, then `SalvageResultJson(state)` ([:230](../../src/Daleel.Web/Conversation/WorkflowSearchRunner.cs)) recovers extracted products (only if worth surfacing), retrying serialization without the heavy `Research` bundle if needed. Only a nothing-usable run throws and surfaces as a hard failure.
- **Audit always flushes** (`RunAsync` `finally`, [:191-196](../../src/Daleel.Web/Conversation/WorkflowSearchRunner.cs)): `PersistAsync` (API calls), `PersistFilteredAsync` (halal audit), `PersistEventsAsync` (events) — each best-effort.
- **Worker resilience + background passes** ([SearchJobService.cs](../../src/Daleel.Web/Conversation/SearchJobService.cs)): one job's failure never sinks the loop; "failed" progress lines stay server-side; after first paint a **detached** `EnrichInBackgroundAsync` ([:196](../../src/Daleel.Web/Conversation/SearchJobService.cs), fresh runs → item deep-dive + live prices) or `ReEnrichInBackgroundAsync` ([:247](../../src/Daleel.Web/Conversation/SearchJobService.cs), `ServeAndEnrich` cache hits → gap-targeted refill) streams the refreshed result via `Enriched`.

---

## 10. Data flow summary

```
Query ─► [1 ParseQuery]    GeoProfile, SearchStrategy
        [2 CheckCache]     (quality-scored hit?) FromCache + ResultJson + CacheQuality ───┐
        [2b AnalyzeMarket] SearchIntelligence (ProductSchema, brands, stores)             │
        [3 GatherSources]  ResearchBundle (web/shopping/stores/social/pages + Prices)     │ on a hit,
        [4 ExtractProducts] Summary + ProductSearchResult (models + Offers/prices)        │ steps
        [5 BrandDispatch]  Brands enriched (Context.dev profile → LLM reputation, save)   │ 2b–10
        [6 StoreDispatch]  Stores enriched (Maps verify, contact, catalogue prices, save) │ no-op
        [7 ItemDispatch]   Models enriched (identify → merge specs → final sheet)         │
        [8 Aggregate]      AgentAnswer { Summary, Research, Products }                    │
        [9 Moderate]       FilteredCount + FilteredCategories                            │
        [10 CacheResults]  ResultJson serialized + persisted ◄──────────────────────────┘
        [11 Return]        CompletedAt
              │
              ▼  SearchRunResult ──► on screen ──► ItemEnrichmentService (live price fill, §6.4)
```

---

## 11. Key files

| Concern | File |
|---------|------|
| Workflow definition | `src/Daleel.Web/Pipeline/SearchWorkflow.cs` |
| The 11 activities | `src/Daleel.Web/Pipeline/SearchActivities.cs` |
| Run state / live services | `src/Daleel.Web/Pipeline/SearchPipelineState.cs`, `SearchPipelineServices.cs` |
| Progress signal / steps | `src/Daleel.Web/Pipeline/SearchProgressSignal.cs` |
| Cache quality validator | `src/Daleel.Web/Pipeline/CacheQualityValidator.cs` |
| Sub-workflows + activities | `src/Daleel.Web/Pipeline/SubWorkflows/*.cs` |
| Fan-out dispatcher | `src/Daleel.Web/Pipeline/SubWorkflows/SubWorkflowDispatcher.cs` |
| **Price aggregation (background)** | `src/Daleel.Web/Pipeline/ItemEnrichmentService.cs` |
| Brand/store research + Google Maps | `src/Daleel.Web/Profiles/ContextDevProfileResearcher.cs` |
| Profile LLM synthesis | `src/Daleel.Web/Profiles/ProfileSynthesizer.cs` |
| Runner (salvage, enrich, re-enrich) | `src/Daleel.Web/Conversation/WorkflowSearchRunner.cs` |
| Background worker | `src/Daleel.Web/Conversation/SearchJobService.cs` |
| LLM methods / search prompts | `src/Daleel.Agent/AgentService*.cs`, `PromptTemplates.cs` |
| Identification / vision / merge | `src/Daleel.Web/Identification/{SmartProductIdentifier,VisionMatcher,SpecMerger}.cs` |
| Providers | `src/Daleel.Search/Providers/*.cs` |
