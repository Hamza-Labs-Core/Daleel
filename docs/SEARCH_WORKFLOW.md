# Search pipeline: Elsa workflow + persistent profiles

This document describes the architecture introduced to (1) persist brand/store profiles researched
via Context.dev and (2) run the search pipeline as an Elsa 3 workflow, plus the decisions made along
the way.

## 1. Persistent Brand & Store profiles

`Brand` and `Store` are EF Core entities in `src/Daleel.Web/Data` (table `Brands`, `Stores`),
persisted to the existing SQLite `DaleelDbContext`.

- **Keyed by a normalized name** (`NameKey` = trimmed, lower-cased) with a unique index, so
  researching the same brand twice **upserts** rather than duplicates.
- **List columns** (pros/cons/popular models, brands carried) persist as JSON via an EF
  `ValueConverter` + `ValueComparer` (so change tracking sees in-place edits).
- **`LastRefreshed` persists as Unix-ms** (`long`) — SQLite cannot translate `DateTimeOffset`
  ordering in a `WHERE` clause, and the staleness sweep filters on exactly that. Same technique the
  existing `SearchCache.ExpiresAt` uses.

`BrandProfileService` / `StoreProfileService` own the lifecycle:

- `GetOrCreateAsync` is **DB-first**: it returns the saved profile and only calls Context.dev + the
  LLM when the profile is **missing or older than the TTL (30 days)**. When research is unavailable
  (no keys) it returns the existing (stale) profile, or `null`.
- `ContextDevProfileResearcher` gathers content via `ContextDevProvider` (brand-intelligence
  endpoint + page scrape) and `ProfileSynthesizer` turns it into a structured entity with the LLM
  (the codebase's standard "ask for JSON, `LlmJson.Deserialize`" pattern). Keys are resolved through
  `IAgentFactory` (new `TryBuildLlm`), identical to the search agent.
- `ProfileRefreshService` (a hosted `BackgroundService`) re-researches stale profiles daily, off the
  request path.

Search results JOIN against this saved data in the `EnrichWithProfiles` workflow step — saved
profiles populate `BrandInfo.Reputation` and store rating/location without re-fetching.

## 2. Elsa 3 search workflow

The pipeline that `AgentService.AskAsync` + `AgentSearchRunner` previously orchestrated by hand is
now an in-process Elsa 3 workflow (`src/Daleel.Web/Pipeline`). Nine `CodeActivity` steps run in
sequence:

1. **ParseQuery** — resolve the market + run the LLM planner into a `SearchStrategy`
2. **CheckCache** — replay a stored report for an identical recent search (short-circuits the rest)
3. **GatherSources** — fan out to all providers in parallel (web/shopping/places/social/scrape)
4. **ExtractProducts** — LLM analyst summary + structured product projection
5. **EnrichWithProfiles** — join extracted brands/stores against saved profiles (DB-first)
6. **AggregateResults** — assemble the ranked `AgentAnswer` + result count
7. **ModerateContent** — record the halal content-filter audit outcome
8. **CacheResults** — serialize + persist the completed report
9. **ReturnResults** — finalize

`WorkflowSearchRunner` is the registered `ISearchRunner`: it builds the agent from server keys, seeds
a scoped `SearchPipelineState`, runs the workflow, and reads the report back. The plan/gather/analyze/
project stages still call the existing (now public) `AgentService` methods — Elsa supplies
orchestration, sequencing and observability, **not new business logic**, so behaviour is unchanged.

### State flow

Heavy domain objects (the gathered bundle, the agent answer) are carried in a **scoped**
`SearchPipelineState` rather than round-tripped through Elsa's serialized `WorkflowState`. Elsa runs
activities in the caller's DI scope, so each activity resolves the same scoped state and services via
`context.GetRequiredService<T>()`. This contract is locked in by `ElsaStateFlowTests`.

## Decisions & deviations

- **Elsa v3 package, not the v2 names in the brief.** `Elsa.Core` / `Elsa.Activities` are Elsa 2
  packages that don't exist in v3. The v3 `Elsa` meta-package supplies the Core engine, the Runtime
  (`AddElsa`, `IWorkflowRunner`) and the built-in activities (`Sequence`) we use.
- **Pinned to Elsa 3.3.3.** Elsa 3.4+ pulls `Microsoft.Extensions.Caching.Abstractions` 9.0, which
  breaks the net8 reference graph (CS1705) in the test project. 3.3.3 keeps the transitive
  `Microsoft.Extensions.*` graph net8-compatible while still providing the full v3 API.
- **Elsa registered core-only** — no Elsa Server / Studio / EF persistence. Workflows run in-process,
  so Elsa adds **no migrations and no extra hosted services**. (The optional Elsa dashboard would
  require the Server + Studio packages and their own persistence store; out of scope here.)
- **Inline enrichment is DB-first and capped** (`MaxEnrich = 12` lookups per search) so a
  brand-heavy query can't fan out unbounded; the background refresh keeps everything current.
- **Schema-safe enrichment.** Saved profiles map onto existing result fields (`BrandInfo.Reputation`,
  store rating/location) so the serialized report shape the UI consumes is unchanged.
