# 01 — Search Pipeline & Workflows

> Source-of-truth reference for how a Daleel search runs end to end: the Elsa orchestration,
> the per-entity sub-workflows, the exact external API calls, every LLM prompt, and the
> error-handling/salvage machinery. Everything here is drawn from the actual source
> (`src/Daleel.Web/Pipeline/**`, `src/Daleel.Web/Conversation/**`, `src/Daleel.Agent/**`,
> `src/Daleel.Search/Providers/**`, `src/Daleel.Web/Identification/**`) — not from intent.
> When the code and this doc disagree, the code wins; fix the doc.

---

## 0. The shape in one paragraph

A user query becomes a queued `SearchJob`. The `SearchJobService` background worker picks it up,
builds a request-scoped `AgentService` from server keys, and hands the job to the
`WorkflowSearchRunner`. That runner creates a DI scope, seeds a `SearchPipelineState` +
`SearchPipelineServices`, and runs the **11-step Elsa `SearchWorkflow`** to completion in a single
in-process pass. Steps 5–7 fan out one **sub-workflow per brand / store / item** in bounded parallel,
each in its own DI scope. The runner reads the assembled `AgentAnswer` back off the state, caches it,
and returns it. After the result is on screen, the worker fires a **detached background enrichment**
(or cache re-enrichment) pass to fill in specs/prices without blocking the queue.

```
SearchJob (queued)
   │
   ▼
SearchJobService.ProcessAsync   ── builds AgentService, streams progress over SignalR
   │
   ▼
WorkflowSearchRunner.RunAsync   ── DI scope, seeds state+services, cost cap, audit logs
   │
   ▼
Elsa SearchWorkflow  (1 → 11, linear Sequence)
   ├─ 1  ParseQuery          plan
   ├─ 2  CheckCache          ←── short-circuits 2b–10 on a quality hit (FromCache=true)
   ├─ 2b AnalyzeMarket       category "thinking"
   ├─ 3  GatherSources       fan out to providers
   ├─ 4  ExtractProducts     LLM analyst + structured extraction
   ├─ 5  DispatchBrandWorkflows   ─┐
   ├─ 6  DispatchStoreWorkflows    ├─ bounded-parallel per-entity sub-workflows
   ├─ 7  DispatchItemWorkflows    ─┘
   ├─ 8  AggregateResults     assemble AgentAnswer
   ├─ 9  ModerateContent      halal audit record
   ├─ 10 CacheResults         serialize + persist
   └─ 11 ReturnResults        finalize
   │
   ▼
SearchRunResult ──► result on screen ──► detached EnrichInBackground / ReEnrichInBackground
```

Defined in [SearchWorkflow.cs](../../src/Daleel.Web/Pipeline/SearchWorkflow.cs).

---

## 1. Workflow overview & the deliberate design

`SearchWorkflow` is a flat Elsa `Sequence` of eleven unconditional `CodeActivity` nodes. There is a
**deliberate architectural tradeoff** spelled out in the class doc-comment ([SearchWorkflow.cs:18-28](../../src/Daleel.Web/Pipeline/SearchWorkflow.cs)):

- The cache-hit short-circuit is **not** modelled as an Elsa `If` edge. Instead, every post-cache
  activity opens with a plain `if (state.FromCache) return;` guard.
- The brand/store/item fan-out is **not** a declarative `ForEach`/parallel activity. It is a
  `Task.WhenAll` inside the dispatch activities.
- Elsa earns its keep here as the **activity-registration + sequencing + per-step telemetry seam**,
  not as a declarative control-flow engine. If real branching ever appears, the comment says to
  either lean into Elsa's `If`/`Flowchart`/`ForEach` or drop Elsa for a plain orchestrator — as a
  conscious migration, not a piecemeal mix.

### Not resumable across suspend

`SearchPipelineState` lives in the **DI scope**, not in Elsa's `WorkflowState`/`Variables`. A real
suspend/resume would build a fresh, blank state from a new scope and silently emit empty results.
So every activity is synchronous (no `Delay`/bookmarks). The payoff of keeping the state pure-data:
Elsa's instance store no longer trips its state-serialization guard, so the runner can persist a
**completed-run summary** (`WorkflowRunSummary`) for the admin workflows page. That is persistence of
*finished* runs, not mid-run suspend/resume. ([SearchPipelineState.cs:17-28](../../src/Daleel.Web/Pipeline/SearchPipelineState.cs))

### The eleven steps

| # | Activity | Role | No-ops when |
|---|----------|------|-------------|
| 1 | `ParseQueryActivity` | Resolve market + LLM-plan the query into a bilingual strategy | — (always runs) |
| 2 | `CheckCacheActivity` | Score & replay a stored report; short-circuit the rest | cache disabled |
| 2b | `AnalyzeMarketActivity` | Up-front category "thinking" (type, stores, brands, spec schema) | `FromCache`, non-product query |
| 3 | `GatherSourcesActivity` | Fan out the strategy across all providers | `FromCache` |
| 4 | `ExtractProductsActivity` | LLM analyst summary + structured product extraction | `FromCache` |
| 5 | `DispatchBrandWorkflowsActivity` | One `BrandResearchWorkflow` per brand (parallel) | `FromCache`, no brands |
| 6 | `DispatchStoreWorkflowsActivity` | One `StoreResearchWorkflow` per store (parallel) | `FromCache`, no stores |
| 7 | `DispatchItemWorkflowsActivity` | One `ItemDeepDiveWorkflow` per model (parallel) | `FromCache`, non-product, no models |
| 8 | `AggregateResultsActivity` | Assemble the final `AgentAnswer` + result count | `FromCache` |
| 9 | `ModerateContentActivity` | Record the halal content-filter audit outcome | `FromCache` |
| 10 | `CacheResultsActivity` | Serialize + persist the report under the result key | `FromCache`, no answer |
| 11 | `ReturnResultsActivity` | Terminal marker; stamp `CompletedAt` | — (always runs) |

All eleven live in [SearchActivities.cs](../../src/Daleel.Web/Pipeline/SearchActivities.cs).

### Progress stepper

Each activity calls `services.Report(SearchStep, key, args…)`, which encodes a structured signal the
client localizes in its own culture. The `SearchStep` enum ([SearchProgressSignal.cs:11-20](../../src/Daleel.Web/Pipeline/SearchProgressSignal.cs)):

| Value | Step | Emoji / meaning |
|-------|------|-----------------|
| 0 | `Analyzing` | 🔍 query analysis + planning |
| 1 | `CheckingVault` | 📦 checking the answers cache |
| 2 | `SearchingWeb` | 🌐 fanning out across providers (with source counts) |
| 3 | `ExtractingProducts` | 🏷️ LLM analyst + product extraction |
| 4 | `BuildingProfiles` | ⭐ joining saved brand profiles |
| 5 | `FindingStores` | 🏪 verifying stores on Google Maps |
| 6 | `ComparingPrices` | 📊 aggregate + moderate + cache (ranking) |
| 7 | `Done` | ✅ finished |

A progress arg of the form `$Some.Resource.Key` tells the client to localize that arg *before*
formatting (used for the translatable query-type noun).

---

## 2. State management — two halves, split on purpose

A single run is described by **two scoped objects**, deliberately split so one is persistable and the
other is never persisted.

### `SearchPipelineState` — pure data ([SearchPipelineState.cs](../../src/Daleel.Web/Pipeline/SearchPipelineState.cs))

Plain data only (primitives, strings, records, lists). Registered **Scoped**. Three bands:

**Inputs** (seeded before the run): `Query`, `Geo` (default `"jordan"`), `Language` (`"en"`),
`ResultKey`, `CacheTtl` (30 days), `SearchId` (the `SearchJob` id, stamped on every event).

**Intermediate results** (filled by activities): `GeoProfile`, `Strategy` (`SearchStrategy`),
`Intelligence` (`SearchIntelligence`), `Bundle` (`ResearchBundle`), `Summary` (string),
`Products` (`ProductSearchResult`), `Answer` (`AgentAnswer`).

**Outputs** (read by the runner): `FromCache`, `CacheQuality` (`[JsonIgnore]`), `ResultJson`,
`ResultType` (`"ask"`), `FilteredCount`, `FilteredCategories`, `ResultCount`.

Plus `Events` — a buffer of non-provider pipeline events (cache hits/misses, profile lookups) the
runner flushes to the event store at the end. `IsProductQuery` is a computed convenience:
`Strategy?.QueryType == QueryType.ProductResearch`.

### `SearchPipelineServices` — the live, non-serializable half ([SearchPipelineServices.cs](../../src/Daleel.Web/Pipeline/SearchPipelineServices.cs))

Holds the references Elsa must **never** persist:

- `Agent` (`AgentService`) — the request-scoped agent driving plan/gather/extract/scrape.
- `Cache` (`ICacheStore?`) — PostgreSQL-backed result cache; null when caching is disabled.
- `Progress` (`Action<string>?`) — the SignalR progress sink.
- `Log(message)` — plain progress line; `Report(step, key, args…)` — structured stepper signal.

Each activity resolves *both* from its execution context:
`context.GetRequiredService<SearchPipelineState>()` and `…<SearchPipelineServices>()`. Splitting the
live half out is what lets the state stay JSON-serializable for the admin instance store.

### Sub-workflow state mirror

The per-entity children mirror the same split: `SubWorkflowState` (pure data base:
`Geo`, `SearchId`, `Events`) + `SubWorkflowServices` (`Agent`, `Progress`/`Log`). Each child runs in
its **own** DI scope — hence its own `DaleelDbContext` — which is exactly what makes the fan-out
concurrency-safe. ([SubWorkflowState.cs](../../src/Daleel.Web/Pipeline/SubWorkflows/SubWorkflowState.cs), [SubWorkflowServices.cs](../../src/Daleel.Web/Pipeline/SubWorkflows/SubWorkflowServices.cs))

---

## 3. Each step in detail

### Step 1 — `ParseQueryActivity` (plan)

**Reads:** `state.Query`, `state.Geo`. **Writes:** `state.GeoProfile`, `state.Geo`, `state.Strategy`.

1. Resolves the market. **The query itself is the strongest market signal** — `GeoProfiles.DetectInText(state.Query)` ("AC in Dubai" → UAE) overrides the stored/auto-detected default; falls back to `GeoProfiles.ResolveOrDefault(state.Geo)`.
2. Calls `services.Agent.PlanAsync(PromptTemplates.PlanFreeform(query, geo), ct)` → a `SearchStrategy` (query type, subject, bilingual web/shopping/social/places/url query lists).
3. Reports the friendly query-type noun (localized client-side via a `Progress.Noun.*` resource key).

`NounKey` maps `QueryType` → resource key (`ProductResearch`→Products, `BrandLookup`→Brand,
`StoreFinder`→Stores, `DealHunter`→Deals, `OpinionAggregation`→Opinions, `Comparison`→Comparison,
else Answers).

### Step 2 — `CheckCacheActivity` (smart cache)

**Reads:** `state.ResultKey`. **Writes (on hit):** `FromCache`, `ResultJson`, `ResultType`,
`FilteredCount`, `FilteredCategories`, `CacheQuality`.

A hit isn't automatically good enough. The flow:

1. If `services.Cache is null` → return (caching disabled).
2. `Cache.GetAsync(ResultKey)`; a null payload or a payload that won't deserialize into `CachedSearchResult` → record `cache.miss`, return (run live).
3. **Score the stored report** with `ICacheQualityValidator.Evaluate(answer)` (see §7). The verdict's `Decision` drives three outcomes:
   - `CacheDecision.Miss` → record `cache.stale`, **leave `FromCache=false`**, fall through to a full live search.
   - `CacheDecision.ServeAndEnrich` → set `FromCache=true`, record `cache.partial`. Report shown immediately; the runner later kicks a background partial re-enrichment for only the missing pieces.
   - `CacheDecision.ServeAsIs` → set `FromCache=true`, record `cache.hit`.

Scoring uses `ResultSerialization.Deserialize<AgentAnswer>` (not raw `JsonSerializer`) so enum fields
round-trip correctly. A corrupt/unscoreable payload defaults to `CacheQualityReport.Complete` — better
to replay a hit than re-run over a scoring hiccup.

**Cancellation rule (recurring across the pipeline):** `OperationCanceledException` is rethrown (a
cap-trip / user cancel must stop the job); any *other* exception is swallowed as a cache miss.

### Step 2b — `AnalyzeMarketActivity` (the "thinking" step)

**Reads:** `Strategy`, `GeoProfile`. **Writes:** `state.Intelligence`. **No-ops** on `FromCache`,
non-product query, or missing geo/strategy.

For a product query, *before any sources are gathered*, it calls
`services.Agent.AnalyzeCategoryAsync(category, geo, ct)` to produce a `SearchIntelligence`: product
type, relevant store types, expected brands, a comparison **`ProductSchema`** (the spec fields that
matter), a price expectation, and whether images matter. This intelligence is threaded into extraction
(schema-aware) and onto the final result. It surfaces reasoning to the user
("Analyzing AC market requirements…", "Looking for electronics and HVAC stores…",
"Extracting BTU, energy rating, cooling specs…"). Records an `intelligence`/`category.analyzed` event.

### Step 3 — `GatherSourcesActivity`

**Reads:** `Strategy`, `GeoProfile`. **Writes:** `state.Bundle`. **No-ops** on `FromCache` / missing inputs.

Calls `services.Agent.GatherAsync(strategy, geo, ct)` → a `ResearchBundle`. This is the deterministic
fan-out across every configured provider (web, shopping, places, social, scrape) in parallel — **no
LLM** here. Reports source counts: `Gathered` (web, shopping, store counts). See §6 for the providers.

### Step 4 — `ExtractProductsActivity`

**Reads:** `Bundle`, `GeoProfile`, `Intelligence`, `Strategy`. **Writes:** `state.Summary`, `state.Products`.

1. Picks the system prompt: `PromptTemplates.ProductAnalystSystem` for product queries, else the default analyst.
2. `services.Agent.AnalyzeAsync(query, geo, bundle, ct, system)` → the narrative `Summary`.
3. For product queries only: `services.Agent.BuildProductSearchResultAsync(subject, geo, bundle, summary, ct, intelligence: state.Intelligence)` → a `ProductSearchResult`. The up-front category `Intelligence` makes extraction **schema-aware** (fills the spec keys that matter) and the schema rides onto the result. Reports `Extracted` product/brand counts.

This step is the up-front assembly of the products the user searched for — which is why the salvage
path (§8) can recover a usable result even if a later step faults.

### Steps 5–7 — Dispatch activities (fan-out)

Each of the three dispatch activities follows the identical shape:

1. Guard: no-op on `FromCache` / empty entity list (item dispatch additionally requires a product query).
2. Take the first *N* entities (caps below), keep the rest un-dispatched.
3. Advance the stepper, then `SubWorkflowDispatcher.RunManyAsync<TWorkflow,TState,TItem>(…)` to fan the children out in bounded parallel, each seeded with the agent, progress sink, geo, and search id.
4. Merge the enriched results with the un-dispatched rest, append each child's buffered `Events` onto `state.Events`, and write `state.Products = products with { … = merged }`.

| Step | Activity | Cap | Sub-workflow |
|------|----------|-----|--------------|
| 5 | `DispatchBrandWorkflowsActivity` | `MaxBrands = 15` | `BrandResearchWorkflow` |
| 6 | `DispatchStoreWorkflowsActivity` | `MaxStores = 10` | `StoreResearchWorkflow` |
| 7 | `DispatchItemWorkflowsActivity` | `MaxItems = 20` | `ItemDeepDiveWorkflow` |

Item dispatch additionally threads `products.Schema` onto each child so the per-item spec merge can
order/rename recognized fields against the category schema. Caps exist so a brand/store/model-heavy
query can't fan out into unbounded research/scrape cost.

### Step 8 — `AggregateResultsActivity`

**Writes:** `state.Answer`, `state.ResultCount`. **No-ops** on `FromCache`.

Assembles the final `AgentAnswer { Question, Geo, QueryType, Summary, Research=Bundle, Products,
GeneratedAt }`. `ResultCount = Products?.ProductCount ?? (WebResults + ShoppingResults).Count`.

### Step 9 — `ModerateContentActivity`

**Writes:** `state.FilteredCount`, `state.FilteredCategories`. **No-ops** on `FromCache`.

The halal filtering itself happens at the gather chokepoint (`ContentFilter`, applied inside
`GatherAsync`); this step only **records the audit**: it reads `services.Agent.ContentFilter.AuditLog`,
sets the filtered count, and distinct categories.

### Step 10 — `CacheResultsActivity`

**Writes:** `state.ResultJson`, `state.ResultType`. **No-ops** on `FromCache` / no answer.

Serializes the answer (`ResultSerialization.Serialize`), records a `cache.write` event, and — if a
cache is configured — writes a `CachedSearchResult(ResultJson, ResultType, FilteredCount,
FilteredCategories)` under `ResultKey` with `CacheTtl`. Cache-write failures are swallowed (a write
must never fail the search); cancellation propagates.

### Step 11 — `ReturnResultsActivity`

**Writes:** `state.CompletedAt`. Terminal marker — outputs already sit on the state. Reports the final
`Done` step (loaded-saved / done-with-count / plain done).

---

## 4. Sub-workflows

Three per-entity Elsa workflows, each dispatched once per entity in bounded parallel by the
`SubWorkflowDispatcher`. The dispatcher (§4.4) gives each child its own DI scope + `DaleelDbContext`,
a hard 30-second timeout, and best-effort semantics: a per-entity timeout or failure leaves the entity
**un-enriched** rather than failing the parent search.

### 4.1 `BrandResearchWorkflow` ([BrandResearchWorkflow.cs](../../src/Daleel.Web/Pipeline/SubWorkflows/BrandResearchWorkflow.cs), [BrandResearchActivities.cs](../../src/Daleel.Web/Pipeline/SubWorkflows/BrandResearchActivities.cs))

State: `BrandResearchState` — `Brand` (input `BrandInfo`), `Result` (output), `Existing`/`Researched`/`Saved`
(`Data.Brand`), `ResolvedFromCache`.

| # | Activity | What it does |
|---|----------|--------------|
| 1 | `SearchBrandSiteActivity` | DB-first: `IBrandRepository.GetByNameAsync`. A **fresh** saved profile (`!IsStale(now, Ttl)`) sets `ResolvedFromCache=true` and skips the paid research. |
| 2 | `ScrapeBrandCatalogActivity` | If not cached and `IProfileResearcher.IsAvailable`: `ResearchBrandAsync(name, geo)` (Context.dev). Records `profile.brand` / `context.dev`. Degrades to the stale saved profile when no keys. |
| 3 | `SynthesizeBrandProfileActivity` | Folds the saved 0–10 profile onto the UI's 1–5 `BrandReputation` (`Score = clamp(r/2, 0, 5)`, `Pros`/`Complaints`/`Summary`), backfills `Url` + the real DB id so the brand page routes by it. |
| 4 | `SaveBrandProfileActivity` | Persists a freshly-researched profile (`UpsertAsync`), backfills the now-persisted DB id. Best-effort. |
| 5 | `DownloadBrandImagesActivity` | **No image R2 bucket is wired** — it records the located logo image(s) (`brand.images`/`r2`, `stored:false`) so a future R2 sink can pick them up, rather than silently doing nothing. |

### 4.2 `StoreResearchWorkflow` ([StoreResearchWorkflow.cs](../../src/Daleel.Web/Pipeline/SubWorkflows/StoreResearchWorkflow.cs), [StoreResearchActivities.cs](../../src/Daleel.Web/Pipeline/SubWorkflows/StoreResearchActivities.cs))

State: `StoreResearchState` — `Store`/`Result` (`StoreInfo`), `Existing`/`Researched`/`Saved`
(`Data.Store`), `ResolvedFromCache`, `PricedProducts`.

| # | Activity | What it does |
|---|----------|--------------|
| 1 | `ScrapeStoreSiteActivity` | DB-first `IStoreRepository.GetByNameAsync`; fresh → reuse. Else `IProfileResearcher.ResearchStoreAsync` (Context.dev). Records `profile.store`. |
| 2 | `VerifyOnMapsActivity` | Folds the Google-Maps-verified `Rating`, `ReviewCount`, `Latitude`/`Longitude` onto the result (live fields preferred, profile backfills). Records `store.verify`/`places`. |
| 3 | `ExtractContactInfoActivity` | Folds `Address` (`Address ?? Location`) and `Phone` onto the result. |
| 4 | `SaveStoreProfileActivity` | Finalizes the `Url` (`Website ?? GoogleMapsUrl`) + DB id; persists a freshly-researched profile. Best-effort. |
| 5 | `ScrapePricesActivity` | The one genuinely new per-store network call: resolves the store domain, instantiates `ContextDevProvider` with `CONTEXT_DEV_API_KEY`, and `ExtractProductsAsync(domain, maxProducts: 12)` (`POST /v1/brand/ai/products`). Counts priced products, records `store.prices`. |

### 4.3 `ItemDeepDiveWorkflow` ([ItemDeepDiveWorkflow.cs](../../src/Daleel.Web/Pipeline/SubWorkflows/ItemDeepDiveWorkflow.cs), [ItemDeepDiveActivities.cs](../../src/Daleel.Web/Pipeline/SubWorkflows/ItemDeepDiveActivities.cs))

The richest child: it scrapes the item's detail page, **identifies** which canonical brand model the
(often vaguely-named) listing actually is, then runs the smart-spec pipeline before price/review
collection. State: `ItemDeepDiveState` — `Model`/`Result` (`ProductModel`), `Key`, `Details`,
`SourceUrl`, `ReusedFromCache`, `Schema` (`ProductSchema`), `IdentifiedBrandModelId`, `Category`,
`MatchConfidence`, `MatchMethod`, `RawSpecsBySource`, `MergedSpecs`, `RawSpecsR2Urls`, `FinalSpecsR2Url`.

The workflow declares the steps in this order (1→9):

| # | Activity | What it does |
|---|----------|--------------|
| 1 | `ScrapeProductPagesActivity` | DB-first `IProductProfileRepository.GetByKeyAsync` (key from `ProductProfile.KeyFor(brand, model, name)`); fresh+non-empty → reuse, no network. Else, a **thin** item (`Specs.Count < 3`) with an offer URL gets `agent.ReadPageAsync(url)`; stores up to 4000 chars of markdown into `Details`. |
| 2 | `IdentifyProductActivity` | `IProductIdentifier.IdentifyAsync(model)` — text → cross-region discovery → cached vision match (§5). On a match, sets `IdentifiedBrandModelId`, `MatchConfidence`, `MatchMethod`, `Category`. Records `item.identify`/(`openrouter` for vision, else `pipeline`). Best-effort. |
| 3 | `ExtractSpecsActivity` | Folds the scraped/reused `Details` markdown into `Result.Specs["details"]`. |
| 4 | `SaveRawSpecsActivity` | Persists **each source's** raw specs + image, routing each to its own R2 bucket: store-listing structured specs → Specs bucket (`raw/{brand}/{model}/store-listing.json`); brand-site specs from the identified catalogue model → Specs bucket (`brand-site.json`); the raw scraped-detail dump → Data bucket (`site-data/{brand}/{model}/scraped-detail.json`). Stages structured sources into `RawSpecsBySource` for the merge. The product image is kept as its original source URL (no download). The DB only ever stores R2 URLs, never raw blobs. |
| 5 | `MergeAndCleanSpecsActivity` | `ISpecMerger.Merge(RawSpecsBySource, Schema)` — dedupe, normalize units, resolve conflicts (brand wins), order/rename against the schema (§5.3). Replaces the as-extracted specs with the canonical sheet. Records `item.merge`. |
| 6 | `SaveFinalSpecsActivity` | Persists the canonical sheet to R2 (`final-specs/{brand}/{model}.json`, Specs bucket) and, when identified and ≤8000 chars, to `BrandModel.FinalSpecsJson` via `SaveFinalSpecsAsync`. Records `item.finalspecs`. |
| 7 | `ComparePricesActivity` | In-memory: counts distinct stores across `Result.Offers`. Records `item.compare`. No network. |
| 8 | `CollectReviewsActivity` | Records the reviews/ratings already gathered (`Result.BrandReputation?.Social`, `ReviewSummary`). Records `item.reviews`. No network. |
| 9 | `SaveItemProfileActivity` | Persists a freshly-scraped deep-dive (`ProductProfile` upsert keyed by `NameKey`) so the next search reuses it. Skips when `ReusedFromCache`. Best-effort. |

> Spec-pipeline R2 writes go through `ItemSpecPipeline` helpers (`SaveJson`, `SaveJsonBlob`,
> `SafeStoreJson`) — all best-effort: an R2 hiccup records nothing and never fails the search.

### 4.4 `SubWorkflowDispatcher` — the fan-out seam ([SubWorkflowDispatcher.cs](../../src/Daleel.Web/Pipeline/SubWorkflows/SubWorkflowDispatcher.cs))

```csharp
DefaultTimeout       = TimeSpan.FromSeconds(30);  // hard per-entity budget
MaxConcurrency       = 5;                          // children running at once
SystematicFailureRatio = 0.5;                      // ≥50% faults ⇒ "systematic"
```

- `RunManyAsync` fans `items` out, at most 5 at a time (a `SemaphoreSlim` gate), each seeded by the caller's lambda (state + services), bounded by the timeout. Returns the finished states **in input order**.
- `RunChildAsync` is the core: a fresh scope → resolve the scoped `TState` + `SubWorkflowServices` → `seed(...)` → run the child via `IWorkflowRunner.RunAsync` under a linked CTS that `CancelAfter(timeout)`. A real outer cancel (`ct.IsCancellationRequested`) rethrows; any other exception (per-entity timeout/fault) returns `(state, faulted: true)` — the entity flows through un-enriched.
- **Systematic-failure detection:** per-entity faults are individually swallowed, which would hide a systematic failure (e.g. Context.dev 401 on every call). So the dispatcher counts faults and, when `faults ≥ max(2, ceil(count × 0.5))`, emits a `⚠️` progress line *and* a `subworkflow_failures`/`dispatcher` WARN event into the parent stream so the failure is observable.

---

## 5. Smart product identification & spec merge

The `ItemDeepDiveWorkflow`'s identify (step 2) and merge (step 5) steps delegate to the
`Daleel.Web.Identification` layer.

### 5.1 `SmartProductIdentifier` ([SmartProductIdentifier.cs](../../src/Daleel.Web/Identification/SmartProductIdentifier.cs))

Result shape: `ProductIdentification(int? BrandModelId, string? CanonicalModelName, string? Category,
double Confidence, string Method)`; `Matched => BrandModelId is not null`; `None = (…, 0.0, "none")`.

Three-stage cascade:

1. **Text match against known models** — look up the brand, text-match the listing's `Model`/`Name` against already-known models (normalized keys / `RegionalAliases`). Hit → **confidence 1.0, method `"text"`**.
2. **Cross-region discovery + retry** — on a miss, `BrandCatalogSearcher` pulls the brand's models across regional sites (Jordan → GCC → global; ≤24 models/region, ≤4 regions, 20s crawl timeout) and re-tries the text match. Hit → **confidence 1.0, method `"text"`**.
3. **Vision match** — last resort: vision-compare the store photo against catalogue images (≤8 comparisons/item, early-accept at confidence ≥ 0.9, returns `None` below the 0.75 threshold). Match → **method `"vision"`, confidence ∈ [0,1]**.

### 5.2 `VisionMatcher` — the OpenRouter vision call ([VisionMatcher.cs](../../src/Daleel.Web/Identification/VisionMatcher.cs))

| Field | Value |
|-------|-------|
| Endpoint | `POST https://openrouter.ai/api/v1/chat/completions` |
| Model | `anthropic/claude-sonnet-4` (`DefaultModel`, overridable) |
| Auth | `Authorization: Bearer {apiKey}` |
| Extra headers | `HTTP-Referer: https://github.com/Hamza-Labs-Core/Daleel`, `X-Title: Daleel` |
| HTTP timeout | 2 minutes |

Request body — system message + a user message whose `content` is an array of one text part and two
`image_url` parts (store image first, brand image second; both passed as full HTTP(S) URLs — non-HTTP
URLs short-circuit to `NoMatch`):

```json
{
  "model": "anthropic/claude-sonnet-4",
  "messages": [
    { "role": "system", "content": "<system prompt below>" },
    { "role": "user", "content": [
      { "type": "text", "text": "<user prompt below>" },
      { "type": "image_url", "image_url": { "url": "<storeImageUrl>" } },
      { "type": "image_url", "image_url": { "url": "<brandImageUrl>" } }
    ] }
  ]
}
```

**System prompt** (verbatim):

```
You are a product-identification expert comparing two product photographs. Decide whether both images show the SAME physical product (same brand, same model line, same variant). Ignore background, watermark, angle and lighting differences. Respond with ONLY a JSON object: {"same_product": boolean, "confidence": number between 0 and 1, "model_name": string}. Set model_name to the specific model you recognize, or null if unsure.
```

**User prompt** (verbatim) — with or without a model hint:

```
The second image is believed to be "{hint}". Is the first image the same product?
```
```
Is the first image the same product as the second image?
```

Expected output (`VisionDto`): `{ "same_product": bool, "confidence": number, "model_name": string|null }`.
Confidence is clamped to `[0,1]`. Results are **memoized globally** per (store-image-hash, brand-model-id)
via the `VisionMatchCache` so an identical comparison never re-calls. No explicit `temperature`/`max_tokens`
(OpenRouter defaults).

### 5.3 `SpecMerger` — dedupe / normalize / resolve / order ([SpecMerger.cs](../../src/Daleel.Web/Identification/SpecMerger.cs))

Source priorities (higher wins conflicts):

```csharp
BrandPriority  = 100;   // brand's official site — WINS
StorePriority  = 50;    // store/marketplace listing
ReviewPriority = 10;    // review / third-party aggregator — LOSES
```

Algorithm:

1. **Gather best candidates.** Iterate sources by descending priority. For each `(key, value)`: normalize the key to lower `snake_case` (regex `[^a-z0-9]+` → `_`, trimmed), normalize the value (`UnitNormalizer`). Skip empties. On a key clash, **keep the higher-priority source's value**; ties keep first-seen.
2. **Apply the schema (when provided).** Re-key recognized specs to the schema's machine `Key` (matched by `Key` or normalized `Label`) and emit them **schema-first, in schema order**; unrecognized specs follow alphabetically. No schema → plain alphabetical.

**Unit normalization** (`UnitNormalizer`) canonicalizes *length* measurements to a dual-unit string
`"{inches} inches / {cm} cm"` (1 inch = 2.54 cm; recognizes `inch/in/"/''`, `cm`, `mm`; rounds to 1
decimal, drops trailing `.0`; culture-invariant). All other specs are whitespace-collapsed but
otherwise unchanged. Example: a brand `"55 inches"` (prio 100) and a store `"139.7 cm"` (prio 50) both
normalize to `"55 inches / 139.7 cm"` under the same `screen_size` key, brand winning the conflict.

---

## 6. Exact external API calls

All providers live in `src/Daleel.Search/Providers/`. Each retries up to **2** times. Keys are
resolved from server environment variables.

### 6.1 SerpAPI — `SerpApiProvider` ([SerpApiProvider.cs](../../src/Daleel.Search/Providers/SerpApiProvider.cs))

`GET https://serpapi.com/search.json` — the primary web/shopping/maps/news engine. The `engine`
parameter is set per `SearchKind` (`google`, `google_shopping`, `google_maps`, `google_news`).

Query parameters (verbatim names): `engine`, `q` (escaped query), `num` (page size, default 10),
`start` (pagination offset 0,10,20…), `safe=active`, `gl` (country code), `hl` (language code),
`location`, and `api_key` (from `SERPAPI_KEY`).

Pagination: pages up to `MaxPages = 10` until it reaches `MaxResults`, hits an empty page, or
deduped results stop growing (dedup by URL or title+position). Parses `organic_results`
(web: title/link/snippet/thumbnail/position), `shopping_results` (+ source/store/rating/price), or
`local_results` (maps: title/website/address/rating).

### 6.2 Google Shopping — `GoogleShoppingProvider`

Not a separate API — an **adapter** over an `ISearchProvider` (SerpAPI) with `SearchKind.Shopping`,
mapping shopping hits into `PricePoint` / `StoreResult` records. No direct external call.

### 6.3 Google Places (New) — `GooglePlacesProvider` ([GooglePlacesProvider.cs](../../src/Daleel.Search/Providers/GooglePlacesProvider.cs))

Base `https://places.googleapis.com`. Auth header `X-Goog-Api-Key` (from `GOOGLE_PLACES_API_KEY`);
the Places API (New) also requires an explicit `X-Goog-FieldMask` per request.

| Endpoint | Method | Purpose |
|----------|--------|---------|
| `/v1/places:searchText` | POST | Text store search (`textQuery`, `languageCode`, optional `locationBias.circle`) |
| `/v1/places:searchNearby` | POST | Nearby search (`includedTypes`, `maxResultCount: 20`, `locationRestriction.circle`) |
| `/v1/places/{placeId}` | GET | Place details (detail field mask) |

Field masks request `id, displayName, formattedAddress, internationalPhoneNumber, websiteUri,
location, googleMapsUri, rating, userRatingCount, priceLevel, regularOpeningHours.weekdayDescriptions,
reviews` (the search variant prefixes each with `places.`). Parsed into `StoreLocation` (price-level
enum `PRICE_LEVEL_*` → 0–4) and `StoreReview`.

### 6.4 Context.dev — `ContextDevProvider` ([ContextDevProvider.cs](../../src/Daleel.Search/Providers/ContextDevProvider.cs))

Base `https://api.context.dev`. Auth header `Authorization: Bearer {CONTEXT_DEV_API_KEY}`. This is the
scraping + brand/catalogue backend (used by `IProfileResearcher`, the store price-scrape, and item
deep-dive page reads).

| Endpoint | Method | Body / query | Returns |
|----------|--------|--------------|---------|
| `/v1/web/scrape/{markdown\|html}?url=…` | GET | `url` query | `ScrapedPage` (markdown/html/content/title) |
| `/v1/web/extract` | POST | `{ url, schema }` (JSON Schema) | extracted object (`data`/`extract`/`result`) |
| `/v1/brand/retrieve?domain=…` | GET | `domain` query | `BrandProfile` (name/description/industry/logo/colors/socials) |
| `/v1/web/crawl` | POST | `{ url, limit: maxPages }` | `ScrapedPage[]` |
| `/v1/brand/ai/products` | POST | `{ domain, maxProducts, timeoutMS }` | `CatalogProduct[]` (name/price/currency/url/category/image/sku) |

The store sub-workflow's price scrape (§4.2 step 5) is the `POST /v1/brand/ai/products` call,
`maxProducts: 12`.

### 6.5 Bing — `BingProvider`

`GET https://api.bing.microsoft.com/v7.0/search` (and `/v7.0/news/search`). Params `q`, `count`,
optional `mkt` (e.g. `ar-JO`). Auth header `Ocp-Apim-Subscription-Key` (from `BING_SEARCH_KEY`). Parses
`webPages.value[]` / `value[]` into `SearchResult`.

### 6.6 OpenSooq — `OpenSooqProvider`

No direct HTTP call. Builds an OpenSooq search URL (`https://{cc}.opensooq.com/en/search?term=…`,
country subdomain `jo/sa/ae/eg`, default `jo`) and routes it through a delegated `IScrapeProvider`
(Context.dev), then regex-extracts `[title](url)` listings + nearby prices into `PricePoint`s.

> **OpenRouter** (the vision call in §5.2) is the only LLM-vision provider; the planning/analysis/
> extraction LLM calls go through `AgentService`'s injected `ILlmClient` (§7).

---

## 7. LLM prompts

All prompts live in [PromptTemplates.cs](../../src/Daleel.Agent/PromptTemplates.cs). Every system
prompt ends with **`HalalGuard`**:

```
 IMPORTANT: Only include halal-compliant results. Exclude any products, stores, or content related to: alcohol/wine, pork/non-halal meat, gambling, adult or immodest content, drugs, or tobacco. If a result promotes any of these, skip it entirely. Do NOT exclude a store merely because it is a bank or offers interest-based (riba) financing — the user can pay cash, so such stores are allowed; only the haram products/content themselves are excluded.
```

There are two LLM roles: a **Planner** (query → bilingual `SearchStrategy`) and an **Analyst**
(gathered results → reports + structured products). Each `AgentService` method that the pipeline calls
is listed below with its prompt.

### 7.1 Planning — `PlanAsync` → `PromptTemplates.PlanFreeform`

System (`PlannerSystem`):

```
You are Daleel, an Arabic-first market-intelligence research planner. Given a user question and a target market, you produce a concrete, bilingual search strategy. You know the Arab world's local platforms (OpenSooq, Haraj, Dubizzle, Talabat, Carrefour, local Facebook groups) and dialects. You ALWAYS reply with a single JSON object only.
```

User (`PlanFreeform`) = market context block + the question + "Classify the question and design the
search strategy." + the **StrategySchema**:

```json
{
  "queryType": "ProductResearch|BrandLookup|StoreFinder|DealHunter|OpinionAggregation|Comparison|General",
  "subject": "the product/brand/topic",
  "webQueries": ["bilingual web search strings"],
  "shoppingQueries": ["shopping/marketplace search strings"],
  "socialQueries": ["social platform keywords"],
  "placesQueries": ["store-finder queries, e.g. 'AC stores', 'متاجر مكيفات'"],
  "urlsToRead": ["specific URLs worth deep-reading, may be empty"],
  "reasoning": "one sentence on the plan"
}
```
> "Generate queries in BOTH the market's primary language and English. No prose outside the JSON."

The market context block (`MarketContext`) is shared by most prompts:
`Market: {country} ({code}). / Languages (priority order): … / Currency: … / Key social platforms: … /
Local marketplaces: … / Main city: ….` (Other planner variants exist — `PlanProduct`, `PlanBrand`,
`PlanStores`, `PlanDeals`, `PlanModel` — used by the typed entry points; the workflow uses
`PlanFreeform`.)

### 7.2 Category intelligence — `AnalyzeCategoryAsync` → `PromptTemplates.CategoryIntelligence`

System (`CategoryIntelligenceSystem`):

```
You are Daleel, a market-research strategist. Before any searching happens, you analyse a product category for a specific market and decide what matters: the precise product TYPE, the kinds of STORE that actually sell it (electronics shops for a TV, not grocery stores), the BRANDS that compete across budget-to-premium tiers, the SPEC fields a buyer compares on (BTU and energy rating for an AC; RAM, storage and camera for a phone; CPU and GPU for a laptop), and a realistic local PRICE expectation. You ground brands and prices in the named market. You ALWAYS reply with a single JSON object only.
```

User = market context + the category + the required JSON object:

```json
{
  "productType": "lower-case product type, e.g. air conditioner",
  "relevantStoreTypes": ["electronics store", "HVAC retailer", "..."],
  "expectedBrands": ["Gree", "Samsung", "LG", "..."],
  "priceExpectation": "short market-aware range, e.g. 'typically 250–1,200 JOD for split units'",
  "imagesMatter": true,
  "specs": [
    { "key": "lower_snake_case_key, e.g. btu", "label": "Human label, e.g. Cooling capacity",
      "unit": "unit suffix like BTU / GB / inch / dB, else null",
      "higherIsBetter": true, "importance": "key|normal" }
  ],
  "reasoning": "one sentence on what decides this purchase"
}
```
> "Choose 4–8 specs that genuinely differentiate this product type (mark the 2–3 defining ones 'key').
> higherIsBetter is true/false/null … No prose outside the JSON."

### 7.3 Analyst summary — `AnalyzeAsync` → `PromptTemplates.Analyze`

For a product query the system prompt is `ProductAnalystSystem`; otherwise the default `AnalystSystem`.

`AnalystSystem`:

```
You are Daleel, an Arabic-and-English market-intelligence analyst. You read search results, social posts, store listings, and reviews, then write clear, decision-ready intelligence. You cite concrete prices, store names, and sentiment. Be concise and honest about uncertainty.
```

`ProductAnalystSystem`:

```
You are Daleel, a shopping assistant. You are given the actual product listings, stores, brands, and review sources gathered for a buy-intent query. Write a short, decision-ready summary. CRITICAL RULES: (1) For each distinct model, aggregate ALL its listings into ONE entry with its multiple price sources — never list the same model several times for several stores. (2) Only mention stores and listings confirmed to be in the target country or that serve it directly; do not present international stores as options unless certain they deliver there. (3) A brand page without prices is still useful — call it a 'Brand catalog'. A store with no visible products is a 'Store — check for availability'. (4) Group concrete options into budget / mid-range / premium and reference real prices from the context — never invent listings or prices. (5) After identifying the products, assess each brand's reputation in the target country — reliability, after-sales service, local warranty, spare-parts availability — and flag any brand with no local service centre, since a cheap product from such a brand is a poor deal.
```

User (`Analyze`) = market context + "Respond in {Language}. Keep product names, brand names, and model
numbers in their original form …" + the task + the flattened bundle between `---` rules + "Write a
clear, decision-ready summary … If the context is thin, say so rather than inventing specifics."

### 7.4 Product extraction — `BuildProductSearchResultAsync` (two LLM calls)

**(a) Extraction** — `ExtractProducts`. System (`ProductExtractionSystem`):

```
You are Daleel, a precise product-extraction engine for a shopping assistant. Given the raw research context for a buy-intent query (search results, shopping hits, store listings, social posts), you EXTRACT the concrete products being sold and their prices. You never write prose, advice, or summaries — only structured data. CRITICAL RULES: (1) Extract ONLY products that are actually evidenced in the context (a name, a price, a seller, or a link); never invent products, prices, models, or links. (2) Output ONE entry per distinct MODEL, with every place it is sold gathered into that entry's offers array — never repeat the same model once per store. (3) Prices are numbers only (strip currency symbols/thousands separators); omit a price you cannot find rather than guessing. (4) Keep brand names, model numbers and product names in their ORIGINAL form (do not translate them). (5) Prefer sellers in the target country; include an offer's link verbatim when the context provides one. You ALWAYS reply with a single JSON object only.
```

User = market context + the buy-intent query + a "Be COMPREHENSIVE …" breadth instruction +
(**when a schema is present**) a "This is a '{productType}' search. In each product's 'specs' object,
PRIORITISE these fields … (use these EXACT keys; omit a field rather than guessing): - {key} ({label}){unit} …"
block + the flattened bundle + the required JSON shape:

```json
{
  "products": [
    { "name": "...", "brand": "...", "model": "model number/name if known, else null",
      "imageUrl": "image link if present in context, else null",
      "specs": { "key": "value" },
      "offers": [ { "source": "...", "price": 320, "currency": "JOD",
                    "url": "direct link if present, else null",
                    "condition": "new|used|refurbished, else null" } ],
      "pros": ["..."], "cons": ["..."],
      "summary": "one-line verdict grounded in the context, else null" }
  ]
}
```
> "One entry per distinct model; gather ALL its sellers into offers … never invent products, prices,
> models, links, or opinions. Use an empty products array if the context has no concrete products.
> Respond ONLY with valid JSON."

**(b) Brand reputation** — `BrandReputations`. System (`BrandReputationSystem`):

```
You are Daleel, a market analyst. You assess how brands are regarded in a SPECIFIC country, with a focus on what matters for a purchase: reliability, after-sales service, local warranty, and spare-parts availability. You flag brands that have NO local service centre, since a cheap product from such a brand is a poor deal. You ALSO surface what REAL people say: from the social posts, forum threads and reviews in the context (especially Arabic ones — تجربتي, رأي, مشاكل), quote actual user opinions with their source, translating non-English quotes to English while keeping the original. Highlight recurring positive and negative themes. Base everything on the provided context and known market facts; be honest about uncertainty and never fabricate quotes. You ALWAYS reply with a single JSON object only.
```

User = "Country: {country} ({code})." + the brand list + a per-brand assessment instruction + the
context, returning a `{ "brands": [ { brand, score (1–5), pros, complaints, hasLocalService,
serviceNote, warranty, summary, reviews:[{quote, originalText, source, url, sentiment, date, language}] } ] }`
shape (reviews only when actually present — never invented).

> A third detail prompt, `ModelProsCons` (system `ModelDetailSystem`), distils a single model's
> pros/cons/summary when needed.

### 7.5 Spec-merge "prompt"

There is **no LLM call** in the spec merge — `SpecMerger` (§5.3) is pure deterministic C# (key
normalization, unit canonicalization, priority-based conflict resolution, schema ordering). The
"smart identification" LLM step is the vision call in §5.2.

---

## 8. Error handling, salvage, retries & timeouts

The pipeline is built so that **everything after product extraction (step 4) is best-effort** — a
fault in enrichment or the serialize tail never throws away the products the user searched for.

### 8.1 Cancellation discipline

A recurring pattern in every `Safe*` helper and every `catch` across the pipeline:
`catch (OperationCanceledException) { throw; }` first, then a bare `catch { … }` that swallows. A
genuine cap-trip / user cancel must stop the whole job; an ordinary failure degrades gracefully.

### 8.2 Cost cap (the cap-trip token)

`WorkflowSearchRunner` reads `CostCaps(MaxPerJob, MonthlyAlert)` from admin config
([CostConfig.cs](../../src/Daleel.Web/Data/CostConfig.cs)) and wires a `JobApiCallCollector` to a
linked `capTrip` CTS. When cumulative API cost exceeds `MaxPerJob` (when `> 0`), the collector logs
"Cost cap … exceeded … stopping search." and **cancels the `capTrip` token**, which propagates as an
`OperationCanceledException` and stops the workflow. The whole workflow runs under `capTrip.Token`.

### 8.3 Per-entity timeouts

Each sub-workflow child runs under a `CancelAfter(30s)` linked CTS (`SubWorkflowDispatcher.DefaultTimeout`).
A timed-out child returns its partial seeded state (entity un-enriched) and is counted as a fault.
The detached enrichment passes (§8.5) run under a `90s` ceiling (`SearchJobService.EnrichTimeout`).

### 8.4 Faulted-run salvage ([WorkflowSearchRunner.cs:138-162, 230-265](../../src/Daleel.Web/Conversation/WorkflowSearchRunner.cs))

Elsa reports `Status == Finished` but a non-`Finished` `SubStatus` on a faulted/cancelled run, leaving
`ResultJson` empty or partial. The runner:

1. Logs the incident messages (WARN) so the underlying fault stays diagnosable.
2. If `ResultJson` is empty, calls `SalvageResultJson(state)`:
   - Take `state.Answer` (or build a fresh `AgentAnswer` over `state.Products`).
   - Only salvage if the products are actually worth surfacing (`HasListings || StoreCount>0 || BrandCount>0 || ReviewCount>0`); else return null.
   - Serialize; **if that throws** (the heavy research bundle — scraped pages / social posts — is the likeliest culprit), retry `with { Research = new ResearchBundle() }` — drop the bundle, keep the products. A usable result beats none.
3. Only a run that produced **nothing usable** throws `InvalidOperationException` and surfaces as a hard failure (→ the user sees "Search failed. Please try again."; the raw message is kept server-side on `SearchJob.Error`).

> This is why a "No results just yet" state can actually be a *faulted* run rather than a genuine empty
> result — the salvage recovers products when it can, and the real exception is server-side only.

### 8.5 Audit logging always runs (`finally`)

`RunAsync`'s `finally` block always flushes — even on a fault partway through:
`PersistAsync` (API-call logs), `PersistFilteredAsync` (halal content-filter audit), and
`PersistEventsAsync` (provider calls projected from the collector + the buffered cache/profile events,
to the optional Postgres event store). Each is itself best-effort (`catch {}`) so audit logging can
never affect the search outcome. The buffered-events reference is captured early
(`bufferedEvents = state.Events`) so it survives a mid-run fault.

### 8.6 Worker-loop resilience & background enrichment ([SearchJobService.cs](../../src/Daleel.Web/Conversation/SearchJobService.cs))

- One job's failure never takes down the worker loop (`catch (Exception) { log }` around `ProcessAsync`); host shutdown rethrows.
- Progress lines containing "failed" are kept **server-side only** — provider/LLM/DB error text (hostnames, partial keys, SQL) never reaches the browser.
- After the base result is on screen, a **detached, fire-and-forget** follow-up runs in its own DI scope under the 90s timeout, so a slow/hanging scrape can't jam the queue:
  - **Fresh run** → `EnrichInBackgroundAsync` → `runner.EnrichAsync` (item deep-dive: official-site specs via Context.dev, fills prices) → streams the refreshed result via `Enriched`.
  - **`ServeAndEnrich` cache hit** → `ReEnrichInBackgroundAsync` → `runner.ReEnrichAsync(job, result, quality)` → refills **only** the gaps the quality validator flagged (thin products, brands missing logo/description, stores missing location/contact/maps), each branch skipped when its dimension is complete; caps `MaxReEnrichBrands=15`, `MaxReEnrichStores=10`.
- Both passes log-only their progress (broadcasting it would flip the UI back to "running" and hide the completed result), overwrite the cached report with the enriched JSON, and re-render in place.

### 8.7 Smart cache quality validator ([CacheQualityValidator.cs](../../src/Daleel.Web/Pipeline/CacheQualityValidator.cs))

The deterministic, side-effect-free scorer behind the §3 step-2 decision. It blends three entity
dimensions, counting a dimension only when entities of that kind exist:

| Dimension | Weight | Per-entity score |
|-----------|--------|------------------|
| Products | 50 | image 0.30 + price 0.30 + specs 0.25 + model 0.10 + SKU 0.05 (specs: ≥3 → 1.0, ≥1 → 0.5, else 0) |
| Brands | 25 | logo 0.6 + description 0.4 |
| Stores | 25 | location 0.5 + contact 0.3 + Google-Maps data 0.2 |

`Score = round(100 × weightedSum / weightTotal)`. A non-product answer or an empty result returns
`CacheQualityReport.Complete` (100) — "empty" is a legitimate outcome, not a degraded entry.

Decision thresholds: `Score ≥ 80` → `ServeAsIs`; `Score < 30` → `Miss`; in between → `ServeAndEnrich`
**if** there are actionable targets (thin products / deficient brands / deficient stores), else
`ServeAsIs` (re-enriching nothing would only burn a background pass). The report also carries the
concrete refill targets (`ThinProducts` indexes, `DeficientBrands`/`DeficientStores` names) and a
`CacheGap` flags set — the exact dimensions the re-enrichment branches on.

---

## 9. Data flow summary

```
Query ─► [1 ParseQuery]    GeoProfile, SearchStrategy
        [2 CheckCache]     (hit?) FromCache + ResultJson + CacheQuality ──────────┐
        [2b AnalyzeMarket] SearchIntelligence (ProductSchema, brands, stores)     │
        [3 GatherSources]  ResearchBundle (web/shopping/stores/social/pages)      │ on a quality
        [4 ExtractProducts] Summary + ProductSearchResult (models/brands/stores)  │ hit, steps
        [5 BrandDispatch]  Brands enriched (Context.dev reputation, DB id)        │ 2b–10 no-op
        [6 StoreDispatch]  Stores enriched (Maps rating/coords, contact, prices)  │
        [7 ItemDispatch]   Models enriched (identify → merge specs → final sheet) │
        [8 Aggregate]      AgentAnswer { Summary, Research, Products }            │
        [9 Moderate]       FilteredCount + FilteredCategories                    │
        [10 CacheResults]  ResultJson serialized + persisted ◄───────────────────┘
        [11 Return]        CompletedAt
                                │
                                ▼
                    SearchRunResult (ResultJson, counts, cost, CacheQuality)
```

`SearchPipelineState` is the single object every step reads and writes; the runner reads the final
outputs (`ResultJson`, `ResultType`, `ResultCount`, `FilteredCount`, `FilteredCategories`,
`CacheQuality`) off it after the run.

---

## 10. Key files

| Concern | File |
|---------|------|
| Workflow definition | `src/Daleel.Web/Pipeline/SearchWorkflow.cs` |
| The 11 activities | `src/Daleel.Web/Pipeline/SearchActivities.cs` |
| Run state / live services | `src/Daleel.Web/Pipeline/SearchPipelineState.cs`, `SearchPipelineServices.cs` |
| Progress signal / steps | `src/Daleel.Web/Pipeline/SearchProgressSignal.cs` |
| Cache quality validator | `src/Daleel.Web/Pipeline/CacheQualityValidator.cs` |
| Sub-workflows | `src/Daleel.Web/Pipeline/SubWorkflows/*.cs` |
| Fan-out dispatcher | `src/Daleel.Web/Pipeline/SubWorkflows/SubWorkflowDispatcher.cs` |
| Item enrichment service | `src/Daleel.Web/Pipeline/ItemEnrichmentService.cs` |
| Production runner (+ salvage, enrich, re-enrich) | `src/Daleel.Web/Conversation/WorkflowSearchRunner.cs` |
| Background worker | `src/Daleel.Web/Conversation/SearchJobService.cs` |
| LLM methods | `src/Daleel.Agent/AgentService*.cs` |
| Prompts | `src/Daleel.Agent/PromptTemplates.cs` |
| Identification / vision / merge | `src/Daleel.Web/Identification/{SmartProductIdentifier,VisionMatcher,SpecMerger}.cs` |
| Providers | `src/Daleel.Search/Providers/*.cs` |
