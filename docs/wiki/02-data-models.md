# Data Models and Storage

> Reference for everything Daleel persists: relational entities, the pipeline event store, Elsa
> workflow instances, Cloudflare R2 object storage, the in-flight search-result models, and the
> caching + migration machinery that ties them together.
>
> Every section below was written against the actual source — file paths are linked so you can jump
> straight to the definition. Nothing here is aspirational; it describes the schema as it ships.

## Storage topology at a glance

Daleel runs on **PostgreSQL** (the SQLite era is gone — see the migration history note at the end).
There are **three EF Core contexts**, two object-storage tiers, and one optional workflow store:

| Concern | Backing store | Context / service | Database |
|---|---|---|---|
| App data (Identity, history, profiles, cache…) | PostgreSQL | [`DaleelDbContext`](../../src/Daleel.Web/Data/DaleelDbContext.cs) | `daleel` |
| Pipeline telemetry (cost/usage audit) | PostgreSQL | [`EventStoreDbContext`](../../src/Daleel.Web/Events/EventStoreDbContext.cs) | `daleel_events` |
| Elsa workflow-instance summaries | PostgreSQL | `ManagementElsaDbContext` (Elsa-shipped) | `daleel_events` (separate `Elsa` schema) |
| Logs / images / specs / scraped data | Cloudflare R2 (S3-compatible) | [`R2StorageService`](../../src/Daleel.Web/Storage/R2StorageService.cs) | 4 buckets |
| Search-result cache | PostgreSQL (`SearchCache` table) | [`PostgresCacheStore`](../../src/Daleel.Web/Data/PostgresCacheStore.cs) | `daleel` |

All three databases sit on the **same Postgres server** sharing one set of credentials; only the
database name differs. The connection is resolved by
[`PostgresConnection`](../../src/Daleel.Web/Events/PostgresConnection.cs), which accepts either a raw
Npgsql keyword string (`POSTGRES_CONNECTION_STRING`) or a URL-form `DATABASE_URL`
(`postgres://user:pass@host:port/db`). `ResolveAppDatabase()` rewrites only the `Database=` keyword to
`daleel` (overridable with `POSTGRES_APP_DATABASE`) so the app's migration history never collides with
the event store's. The event store and the Elsa store share the base connection's database
(`daleel_events` by convention) but stay separate because Elsa lives in its own `Elsa` schema with its
own `__EFMigrationsHistory`.

---

## 1. Entity models (`DaleelDbContext`)

[`DaleelDbContext`](../../src/Daleel.Web/Data/DaleelDbContext.cs) inherits
`IdentityDbContext<ApplicationUser>`, so it carries the **full ASP.NET Core Identity schema**
(`AspNetUsers`, `AspNetRoles`, `AspNetUserRoles`, `AspNetUserLogins` for external providers,
`AspNetUserClaims`, `AspNetUserTokens`, …) on top of the app-owned `DbSet`s below.

### A recurring storage convention: Unix-ms `bigint` timestamps

Several `DateTimeOffset` columns are persisted as **Unix-millisecond `bigint`** via an EF
`ValueConverter` (`toUnixMs`), rather than as native `timestamptz`. This is a deliberate,
provider-agnostic choice kept across the SQLite→Postgres migration: every range/order filter (`<`,
`<=`, `>= since`) the staleness sweeps and usage aggregates run translates identically regardless of
provider. Columns using it: `Brand/Store/ProductProfile/BrandModel.LastRefreshed`,
`BrandModel.DiscoveredAt`, `ScrapedPrice.ScrapedAt`, `ApiCallLog.CreatedAt`,
`SearchCache.{CreatedAt,ExpiresAt}`, `FilteredContentLog.CreatedAt`. (`AnalyticsEvent.Timestamp` uses
a plain `DateTime` for the same range/group reason; the event store's `PipelineEvent.Timestamp` uses
native `timestamptz`.)

JSON list columns (`List<string>`) are stored as serialized JSON text with a paired `ValueComparer`
so EF detects in-place list mutations.

### User & identity

#### `ApplicationUser` — [`ApplicationUser.cs`](../../src/Daleel.Web/Data/ApplicationUser.cs)
Extends `IdentityUser` (table `AspNetUsers`).

| Property | Type | Notes |
|---|---|---|
| `DisplayName` | `string?` | Defaults to local part of email at registration |
| `AvatarUrl` | `string?` | Optional profile picture |
| `IsAdmin` | `bool` | Admin role flag (aggregate stats only — never row data) |
| `IsDisabled` | `bool` | Admin-disabled account; blocks sign-in |
| `CreatedAt` | `DateTime` | UTC; powers "registered today/week/month" stats |
| `LastActiveAt` | `DateTime?` | UTC last sign-in |
| `EmailSearchResults` | `bool` (default `true`) | Opt-out email-on-completion. SQL default `true` backfills existing rows; read server-side by the background worker, so it must be DB state |

### Search history & saved results

#### `SearchHistoryEntry` — [`SearchHistoryEntry.cs`](../../src/Daleel.Web/Data/SearchHistoryEntry.cs)
One row recorded automatically every time a query completes. Indexed on `(UserId, CreatedAt)`.

| Property | Type | Notes |
|---|---|---|
| `Id` | `int` | PK |
| `UserId` | `string` | Owner; every read filters on it |
| `Query` | `string` (max 2000) | Raw user query |
| `QueryType` | `string` (max 32) | `ask`/`brand`/`stores`/`deals`/`product`/`compare`/`reviews` |
| `Geo` | `string` (max 64) | Market key, e.g. `jordan` |
| `Model` | `string` (max 128) | OpenRouter model id used |
| `ResultSummary` | `string?` (max 1000) | Truncated one-line preview |
| `ResultJson` | `string?` | Full serialized result so reopening re-renders instantly (null for old rows → re-run) |
| `CreatedAt` | `DateTimeOffset` | |
| `SavedResults` | `ICollection<SavedResult>` | Back-reference |

#### `SavedResult` — [`SavedResult.cs`](../../src/Daleel.Web/Data/SavedResult.cs)
A result the user explicitly bookmarked. Indexed `(UserId, CreatedAt)`. FK to `SearchHistory` with
**`OnDelete: SetNull`** — the saved copy survives even if its originating history row is deleted.

| Property | Type | Notes |
|---|---|---|
| `Id` | `int` | PK |
| `UserId` | `string` | Owner |
| `SearchHistoryId` | `int?` | Optional link back to history |
| `Title` | `string` (max 300) | Defaults to query, editable |
| `ResultJson` | `string` | Complete serialized result blob |
| `ResultType` | `string` (max 32) | Discriminator for deserialization |
| `Notes` | `string?` | Free-text user notes |
| `CreatedAt` | `DateTimeOffset` | |

### Async conversation / job model

#### `SearchJob` — [`SearchJob.cs`](../../src/Daleel.Web/Data/SearchJob.cs)
A unit of async search work. Created by `POST /api/search`, picked up by the background
`SearchJobService`, streamed over SignalR. The HTTP request never runs the 30–60s agent itself.
Indexed `(UserId, Status)`. Status values come from `JobStatus`: `queued`, `running`, `completed`,
`failed`, `cancelled`.

| Property | Type | Notes |
|---|---|---|
| `Id` | `int` | PK |
| `UserId`, `Query` | `string` | required |
| `QueryType` | `string` (default `ask`) | |
| `Geo` | `string` (default `jordan`) | |
| `Model` | `string` | |
| `Language` | `string` (default `en`) | BCP-47 UI language for the summary |
| `Status` | `string` (max 20) | see `JobStatus` |
| `ProgressMessage` | `string?` | Live progress line |
| `ResultJson` | `string?` | Final serialized result |
| `Error` | `string?` | Failure cause (shown on `/admin/workflows`) |
| `CreatedAt`/`StartedAt`/`CompletedAt` | `DateTimeOffset(?)` | lifecycle stamps |

#### `UserConversation` — [`SearchJob.cs`](../../src/Daleel.Web/Data/SearchJob.cs) (same file)
A user's **single active conversation** (ChatGPT-style: one per user, keyed by `UserId` as PK).
Replaced on each new search, persists across sessions, is the source of truth every device renders on
load. Managed by [`ConversationStore`](../../src/Daleel.Web/Conversation/ConversationStore.cs).

| Property | Type | Notes |
|---|---|---|
| `UserId` | `string` | **PK** |
| `CurrentJobId` | `int?` | Active job |
| `CurrentQuery` | `string?` | |
| `CurrentStatus` | `string` (default `idle`) | `idle`/`running`/`completed`/`error` — mirrors active job |
| `CurrentResultJson` | `string?` | |
| `CurrentResultType` | `string?` | |
| `StartedAt`/`CompletedAt` | `DateTimeOffset?` | |

### Persisted research profiles

These four are the heart of "research once, reuse forever". Each is upsert-keyed on a normalized name
and refreshed when stale (`IsStale(now, ttl)`).

#### `Brand` — [`Brand.cs`](../../src/Daleel.Web/Data/Brand.cs)
A periodically-refreshed brand profile (built by `BrandProfileService` via Context.dev + LLM).
Unique index on `NameKey`; index on `LastRefreshed` (staleness sweep). Default TTL 30 days.

| Property | Type | Notes |
|---|---|---|
| `Id` | `int` | PK |
| `Name` (max 200) / `NameKey` (max 200, unique) | `string` | `NameKey` = trimmed, lower-cased |
| `CountryOfOrigin` | `string?` (max 100) | |
| `ReputationScore` | `double?` | 0–10, LLM-assessed |
| `Description` | `string?` (max 4000) | |
| `Pros` / `Cons` / `PopularModels` | `List<string>` | JSON columns |
| `PriceRange` | `string?` (max 100) | free-text positioning |
| `Website` | `string?` (max 500) | |
| `LastRefreshed` | `DateTimeOffset` | Unix-ms |

#### `Store` — [`Store.cs`](../../src/Daleel.Web/Data/Store.cs)
Store/retailer profile (the store-side counterpart to `Brand`), built by `StoreProfileService`.
Unique index on `NameKey`; index on `LastRefreshed`. Carries a **Google-Places verification block**.

| Property | Type | Notes |
|---|---|---|
| `Id` | `int` | PK |
| `Name` / `NameKey` (unique) | `string` (max 200) | |
| `Location` (max 300) / `Type` (max 100) / `Website` (max 500) | `string?` | |
| `BrandsCarried` | `List<string>` | JSON; cross-links brand↔store |
| `Rating` | `double?` | 0–5, LLM-assessed |
| `Phone` (max 64) / `Email` (max 256) / `Address` (max 500) | `string?` | contact fields |
| `Latitude` / `Longitude` | `double?` | verified Places coordinates |
| `OpeningHours` | `List<string>` | JSON; Places `weekdayDescriptions` |
| `GoogleRating` | `double?` | Maps aggregate (distinct from `Rating`) |
| `GoogleReviewCount` | `int?` | |
| `GooglePlaceId` (max 256) / `GoogleMapsUrl` (max 500) | `string?` | |
| `LastRefreshed` | `DateTimeOffset` | Unix-ms |
| `IsVerified` (computed) | `bool` | true when `GooglePlaceId` set |

#### `ProductProfile` — [`ProductProfile.cs`](../../src/Daleel.Web/Data/ProductProfile.cs)
A product/model deep-dive (scraped offer page). Upsert-keyed by `NameKey` (normalized brand+model via
`StableId.NormalizeIdentity`, shared with `ScrapedPrice.ProductKey`). Unique index on `NameKey`; index
on `LastRefreshed`.

| Property | Type | Notes |
|---|---|---|
| `Id` | `int` | PK |
| `Name` (max 300) / `NameKey` (max 300, unique) | `string` | |
| `Brand` (max 200) / `Model` (max 200) | `string?` | |
| `Details` | `string?` (max 8000) | scraped markdown specs/description |
| `SourceUrl` | `string?` (max 1000) | |
| `LastRefreshed` | `DateTimeOffset` | Unix-ms |

#### `BrandModel` — [`BrandModel.cs`](../../src/Daleel.Web/Data/BrandModel.cs)
One product model belonging to a `Brand`, harvested from the brand's own site — the per-model row that
builds a searchable model database (specs, image, local-vs-global price). Also the home of the
**smart product-identification (vision pipeline)** columns. Unique index on `(BrandId, ModelKey)`;
index on `LastRefreshed`. **FK to `Brand` with `OnDelete: Cascade`**.

| Property | Type | Notes |
|---|---|---|
| `Id` | `int` | PK |
| `BrandId` | `int` | FK → `Brand` (cascade) |
| `ModelName` (max 300) / `ModelKey` (max 300) | `string` | `(BrandId, ModelKey)` unique |
| `Category` | `string?` (max 120) | e.g. "Smartphone" |
| `SpecsJson` | `string?` (max 8000) | raw scraped specs JSON |
| `ImageUrl` | `string?` (max 1000) | R2-hosted copy or original source |
| `LocalPrice` / `GlobalPrice` | `decimal?` `(18,2)` | regional vs reference price |
| `Currency` | `string?` (max 16) | |
| `IsAvailable` | `bool` | |
| `SourceUrl` | `string?` (max 1000) | |
| `LastRefreshed` | `DateTimeOffset` | Unix-ms |
| `FinalSpecsJson` | `string?` (max 8000) | **canonical merged spec sheet** the UI reads (never raw) |
| `FinalSpecsR2Url` | `string?` (max 1000) | R2 pointer `final-specs/{brand}/{model}.json` |
| `ImageR2Urls` | `List<string>` | JSON; every discovered image (for vision matching across the full catalogue) |
| `RegionalAliases` | `List<string>` | JSON; regional SKUs/aliases that resolve to this row |
| `DiscoveredAt` | `DateTimeOffset` | Unix-ms; set once |
| `IsDiscontinued` | `bool` | retained for historical matching |

#### `VisionMatchCache` — [`VisionMatchCache.cs`](../../src/Daleel.Web/Data/VisionMatchCache.cs)
A memoized verdict of one vision-model comparison between a store product image and a brand catalogue
model. Vision LLM calls are slow + paid, so each `(StoreImageHash, BrandModelId)` pair is matched
once and kept forever (negative matches too). **Unique index** on the pair; **FK to `BrandModel`,
`OnDelete: Cascade`**.

| Property | Type | Notes |
|---|---|---|
| `Id` | `int` | PK |
| `StoreImageHash` | `string` (max 64) | SHA-256 hex of the store image URL/bytes (stable across CDN URL churn) |
| `BrandModelId` | `int` | FK → `BrandModel` (cascade) |
| `Confidence` | `double` | 0.0–1.0 vision confidence |
| `MatchedModelName` | `string?` (max 300) | |
| `MatchedAt` | `DateTimeOffset` | Unix-ms |
| `MatchThreshold` (const) | `0.75` | `IsMatch` ⇔ `Confidence ≥ 0.75` |

#### `ScrapedPrice` — [`ScrapedPrice.cs`](../../src/Daleel.Web/Data/ScrapedPrice.cs)
**Append-only time series** of price observations (unlike the upsert profiles). Every scrape writes a
fresh row; the latest per `(product, store)` is the current price. Indexes: `(ProductKey, ScrapedAt)`
(hot "latest prices for this model" read) and `ScrapedAt` (recency sweep).

| Property | Type | Notes |
|---|---|---|
| `Id` | `long` | PK |
| `ProductName` (max 300) / `ProductKey` (max 300) | `string` | `ProductKey` shared with `ProductProfile.KeyFor` |
| `StoreName` | `string` (max 200) | |
| `Price` | `decimal?` `(18,2)` | null when only availability was readable |
| `Currency` | `string?` (max 16) | |
| `SourceUrl` | `string?` (max 1000) | |
| `Provider` | `string` (max 64) | `context.dev` or `cloudflare-browser` |
| `ScrapedAt` | `DateTimeOffset` | Unix-ms |

### Subscriptions, quota & billing

#### `SubscriptionPlan` — [`SubscriptionPlan.cs`](../../src/Daleel.Web/Data/SubscriptionPlan.cs)
Admin-configurable tier. **Seeded via `HasData`** with three stable ids: `Basic` (1, 500 credits,
free), `Pro` (2, 5000 credits, $9.99), `Unlimited` (3, 50000 credits, $100). All plans unlock the same
features — only the credit allowance differs.

| Property | Type | Notes |
|---|---|---|
| `Id` | `int` | PK (`BasicId=1`, `ProId=2`, `UnlimitedId=3`) |
| `Name` | `string` | |
| `SearchesPerMonth` | `int?` | **Legacy** — superseded by `MonthlyCredits`, kept for old rows |
| `MonthlyCredits` | `int?` | The real gate; `null` ⇒ unlimited (`IsUnlimited`) |
| `PriceMonthly` | `decimal` `(10,2)` | |
| `PriceYearly` | `decimal?` `(10,2)` | |
| `FeaturesJson` | `string` (default `[]`) | JSON array of bullet strings; `GetFeatures()`/`SetFeatures()` decode/encode |
| `IsActive` | `bool` (default true) | |
| `SortOrder` | `int` | |

#### `UserSubscription` — [`UserSubscription.cs`](../../src/Daleel.Web/Data/UserSubscription.cs)
Links a user to their current plan. Indexed on `UserId`. FK to `SubscriptionPlan`. Stripe fields
reserved.

| Property | Type | Notes |
|---|---|---|
| `Id` | `int` | PK |
| `UserId` | `string` | |
| `PlanId` | `int` | FK → `SubscriptionPlan` |
| `Status` | `string` (max 20) | `active`/`cancelled`/`expired` |
| `StartedAt` / `ExpiresAt` | `DateTimeOffset(?)` | |
| `StripeSubscriptionId` | `string?` | reserved for billing |

#### `UserQuota` — [`UserSubscription.cs`](../../src/Daleel.Web/Data/UserSubscription.cs) (same file)
Per-user usage counter for the current billing period. **Unique index** on `UserId`.

| Property | Type | Notes |
|---|---|---|
| `Id` | `int` | PK |
| `UserId` | `string` | unique |
| `SearchesUsed` | `int` | **Legacy** counter, superseded by `CreditsUsed` |
| `CreditsUsed` | `int` | Live gate; resets per period |
| `QuotaLimit` | `int?` | Resolved monthly limit (`null` = unlimited) |
| `PeriodStart` / `PeriodEnd` | `DateTimeOffset` | billing window |

### Analytics, logging & config

#### `AnalyticsEvent` — [`AnalyticsEvent.cs`](../../src/Daleel.Web/Data/AnalyticsEvent.cs)
Append-only admin-dashboard analytics; never touches user-owned tables. Indexed `(EventType,
Timestamp)`. **IPs stored hashed, never raw.**

| Property | Type | Notes |
|---|---|---|
| `Id` | `int` | PK |
| `EventType` | `string` (max 20) | `search`/`login`/`pageview` |
| `UserId` | `string?` | null for anonymous |
| `Query` (max 2000) / `QueryType` / `Geo` / `Model` / `Path` / `Provider` | `string?` | context |
| `IpHash` | `string?` | SHA-256 truncated of client IP |
| `DurationMs` / `ResultCount` / `ApiCallsMade` | `int?` | |
| `FilteredCount` | `int?` | results removed by the halal filter |
| `FilteredCategories` | `string?` | comma-separated tripped categories |
| `Timestamp` | `DateTime` | UTC (plain `DateTime`, not offset) |

#### `SystemConfig` — [`AnalyticsEvent.cs`](../../src/Daleel.Web/Data/AnalyticsEvent.cs) (same file)
Typed key/value system setting editable at `/admin/settings`. Seeded idempotently at startup by
`ISystemConfigService.SeedDefaultsAsync()`.

| Property | Type | Notes |
|---|---|---|
| `Key` | `string` | **PK** |
| `Value` | `string` | |
| `Type` | `string` (default `string`) | `string`/`int`/`bool`/`json` editor hint |

#### `ApiCallLog` — [`ApiCallLog.cs`](../../src/Daleel.Web/Data/ApiCallLog.cs)
Record of one external API call during a search — basis for per-user usage, per-provider analytics,
cost tracking. Indexes: `JobId` (live UI log), `(UserId, CreatedAt)` (usage/cost), `(Provider,
CreatedAt)`. (This is also the default `IEventStore` backend when used in SQLite mode — see
[memory: event store dual backend].)

| Property | Type | Notes |
|---|---|---|
| `Id` | `long` | PK |
| `UserId` | `string?` | search owner |
| `JobId` | `int?` | owning `SearchJob` |
| `Provider` (max 64) / `Endpoint` (max 64) | `string` | required |
| `RequestSummary` | `string?` (max 500) | non-sensitive summary |
| `ResponseTimeMs` / `ResponseBytes` | `long` | |
| `Status` | `string` (max 16) | `success`/`error`/`timeout` |
| `EstimatedCost` | `decimal` `(12,6)` | |
| `Model` (max 128) / `InputTokens` / `OutputTokens` | `string?`/`int?` | |
| `CreatedAt` | `DateTimeOffset` | Unix-ms |

#### `FilteredContentLog` — [`FilteredContentLog.cs`](../../src/Daleel.Web/Data/FilteredContentLog.cs)
Admin-only audit of content the halal filter removed. **Deliberately carries no `UserId`** — the table
is anonymous by construction. Indexes: `CreatedAt`, `(Category, CreatedAt)`.

| Property | Type | Notes |
|---|---|---|
| `Id` | `long` | PK |
| `Query` (max 2000) / `Geo` (max 64) | `string?` | |
| `Category` | `string` (max 32) | blocked category, e.g. `alcohol` |
| `Rule` | `string?` (max 128) | exact blocklist term that matched |
| `Kind` | `string?` (max 64) | `text`/`SearchResult`/`StoreLocation`… |
| `Content` | `string?` (max 300) | truncated offending snippet |
| `CreatedAt` | `DateTimeOffset` | Unix-ms |

#### `SearchCache` — [`SearchCache.cs`](../../src/Daleel.Web/Data/SearchCache.cs)
The cache table (detailed in §6). Unique index on `CacheKey`; `(Layer, ExpiresAt)` for the purge sweep.

---

## 2. Event store (pipeline telemetry)

A **second, deliberately separate** PostgreSQL context records every external pipeline action for the
admin usage/cost dashboard. Holding the event firehose off the transactional app DB lets it scale and
retain independently.

- [`EventStoreDbContext`](../../src/Daleel.Web/Events/EventStoreDbContext.cs) — single table
  `pipeline_events`. Indexes: `Timestamp`, `(Category, Timestamp)`, `(Provider, Timestamp)`,
  `SearchId`. `MetadataJson` is a native **`jsonb`** column; `EstimatedCost` is `decimal(12,6)`.
- [`PipelineEvent`](../../src/Daleel.Web/Events/PipelineEvent.cs) — the append-only row (written,
  never updated):

| Property | Type | Notes |
|---|---|---|
| `Id` | `long` | PK |
| `Timestamp` | `DateTimeOffset` | `timestamptz` |
| `Category` | `string` (max 32) | one of `EventCategory` |
| `EventType` | `string` (max 64) | e.g. `shopping`, `scrape/markdown`, `chat`, `cache.hit` |
| `Provider` | `string` (max 96) | `SerpAPI`/`Context.dev`/`Google Places`/`OpenRouter/…` |
| `SearchId` | `string?` (max 64) | correlates a run = the `SearchJob.Id`; null for ad-hoc |
| `DurationMs` | `long` | |
| `EstimatedCost` | `decimal` `(12,6)` | USD; 0 for free actions (cache hits) |
| `Success` | `bool` (default true) | |
| `MetadataJson` | `string` (`jsonb`) | free-form detail (`"{}"` when empty) |

`EventCategory` fixed set: `search`, `scrape`, `extract`, `places`, `llm`, `profile`, `cache`.

**Implementation** ([`PostgresEventStore`](../../src/Daleel.Web/Events/PostgresEventStore.cs),
interface [`IEventStore`](../../src/Daleel.Web/Events/IEventStore.cs)):
- Uses `IDbContextFactory<EventStoreDbContext>` — a fresh context per call (the pipeline runs
  providers in parallel).
- **All writes are best-effort**: a Postgres hiccup is swallowed and logged so it never fails a user's
  search.
- Read shapes: `RecordBatchAsync` (bulk insert), `GetUsageAsync(since)` (window rolled up in-memory by
  the pure `UsageReport.Build`), `RecentAsync(take)` (live tail, clamped 1–500), `ForSearchAsync` (one
  run's timeline, oldest-first), `SummarizeBySearchAsync` (batched per-search cost/error rollup to
  avoid an N+1 on the workflows list).
- `NullEventStore` is the no-op used when Postgres isn't configured (drops every event; `IsEnabled =
  false`, dashboard shows a "not configured" hint).

---

## 3. Elsa workflow persistence

The search pipeline runs as an **in-process Elsa 3 workflow** of `CodeActivity` steps:
`plan → cache → analyze → gather → extract → dispatch brand/store/item sub-workflows → aggregate →
moderate → cache → return`. `AddActivitiesFrom` scans the whole assembly so the sub-workflow
activities under `Pipeline/SubWorkflows` are discovered automatically.

**What Elsa persists** (see [`Program.cs`](../../src/Daleel.Web/Program.cs) ~L367–397):
- Workflow-instance persistence is **optional and Postgres-only**. When a Postgres connection is
  configured, `elsa.UseWorkflowManagement(... UsePostgreSql(...))` is registered, which provides
  `IWorkflowInstanceStore` + `IWorkflowInstanceManager` and the Elsa-shipped `ManagementElsaDbContext`
  (tables `WorkflowDefinitions`, `WorkflowInstances`, etc., in the `Elsa` schema of the
  `daleel_events` database).
- When Postgres is **not** configured, the management feature isn't registered — there is **no
  in-memory or SQLite fallback**. The search workflow still runs in-process via `IWorkflowRunner`; the
  runner simply skips its best-effort instance save and `/admin/workflows` shows a "not configured"
  notice.

**Critical caveat (from the source comments and [memory: elsa-pipeline-persistence]):** this persists
**completed-run summaries** for the admin page, **not** mid-run suspend/resume. The working run state
lives in the DI scope (`SearchPipelineState` / `SearchPipelineServices`), **not** in Elsa's serialized
`WorkflowState`. The workflow must run to completion in one pass — **do not add Delay/bookmark/suspend
activities**, since a resume would see blank state.

Elsa's auto-migration only fires under `UseWorkflowRuntime` (which Daleel does **not** use), so the
`ManagementElsaDbContext` schema is migrated **explicitly at startup** in `EnsureDatabase` — see §7.
(This fixed the `42P01: relation "Elsa.WorkflowInstances" does not exist` error — commit `3cf1ded`.)

---

## 4. R2 storage (4 buckets)

Cloudflare R2 (S3-compatible) stores large/binary artifacts outside the database. The whole feature is
**best-effort and optional**: every method degrades to the original URL / a null when R2 isn't
configured or a transfer fails, and **never throws** into the caller.

- [`R2Bucket`](../../src/Daleel.Web/Storage/R2Options.cs) enum + per-bucket config; routed by
  [`R2StorageService`](../../src/Daleel.Web/Storage/R2StorageService.cs) (one shared S3 client; only
  `BucketName` per request differs). `NullR2StorageService` is the no-op when unconfigured.

| Bucket | Default name | Public host var | What goes in it | Served publicly? |
|---|---|---|---|---|
| `Logs` | `daleel-logs` | — (never) | Serilog JSON-Lines error logs | No — admins read via presigned GET |
| `Images` | `daleel-images` | `R2_PUBLIC_URL_IMAGES` (or legacy `R2_PUBLIC_URL`) | Product images, brand logos, store photos | Yes (when host set) |
| `Specs` | `daleel-specs` | `R2_PUBLIC_URL_SPECS` | Raw + final canonical product-spec JSON | Optional |
| `Data` | `daleel-data` | `R2_PUBLIC_URL_DATA` | Site-data, brand catalogs, scraped-data dumps | Optional |

**Configuration** ([`R2Options.FromConfiguration`](../../src/Daleel.Web/Storage/R2Options.cs)): the
gate is the **shared connection** — `R2_ACCESS_KEY` + `R2_SECRET_KEY` + a resolvable endpoint
(`R2_ENDPOINT`, or derived from `CLOUDFLARE_ACCOUNT_ID` as
`https://{account}.r2.cloudflarestorage.com`). Bucket names always resolve because they default to the
`daleel-*` names; operators just create the buckets. The `CHANGE_ME` placeholder reads as "not
configured". Legacy single-bucket vars (`R2_BUCKET_NAME`, `R2_PUBLIC_URL`) are honoured as fallbacks.

**Key patterns & behaviours:**
- **Images** (`StoreImageAsync(sourceUrl, keyPrefix)`): downloads the source and uploads to the
  `Images` bucket; key is `{keyPrefix}/{sha256(sourceUrl)[..16]}{ext}` — deterministic, so re-running
  enrichment overwrites in place. Returns `{publicUrl}/{key}`. Degrades to the original URL when: no
  public host (an `<img>` pointing at the S3 API endpoint would 403), already R2-hosted, blocked by the
  **SSRF guard** (scraped/LLM-supplied URLs are untrusted), >8 MB, or any failure. Reads are stream-
  capped so a lying/absent `Content-Length` can't OOM the host.
- **JSON** (`StoreJsonAsync(json, objectKey, bucket = Specs)`): uploads to the given bucket at a
  human-readable key (e.g. `final-specs/{brand}/{model}.json`). The object is **always stored** even
  when the bucket is private (the admin viewer reads it back via presigned GET); a hosted URL is
  returned only when a public host exists, else `null` (stored-but-not-hot-linkable — the DB copy stays
  canonical). Cap 4 MB. `objectKey` is sanitized (lower-cased, slash-segmented, illegal chars → `-`).
- **Listing/reading** (`ListObjectsAsync`, `ReadTextAsync`, `DownloadUrl`): power the admin Data
  viewer. Listing uses delimiter `/` for a one-level "folder" view; `DownloadUrl` mints a 15-minute
  presigned **SigV4** GET (R2 rejects SigV2 — a process-global toggle forces SigV4). `DisablePayloadSigning = true` on PUTs (R2 rejects SigV4 streaming payload signing).

> R2 multi-bucket gotchas worth knowing (from [memory: admin-r2-and-workflow-status]): images are
> served from `Images`/its public host; `StoreJsonAsync` now uploads regardless of public host; the
> admin Data browser defaults to the `Data` bucket.

---

## 5. Search-result models (in-flight)

These are the **in-memory result shapes** the agent produces and the cache serializes — distinct from
the persisted entities above. They live in `Daleel.Core.Models` and `Daleel.Agent`.

### `AgentAnswer` — [`AgentResults.cs`](../../src/Daleel.Agent/AgentResults.cs)
The top-level answer to a query. Serialized into `SearchJob.ResultJson`, `SearchHistoryEntry.ResultJson`,
`SavedResult.ResultJson`, and the result-layer cache.

| Property | Type | Notes |
|---|---|---|
| `Question` / `Geo` | `string` | |
| `QueryType` | `QueryType` | enum |
| `Summary` | `string` | LLM narrative |
| `Research` | `ResearchBundle` | raw gathered data (web/shopping results, stores, social, prices, scraped pages, sources) |
| `Products` | `ProductSearchResult?` | present only for product searches — drives the product grid |
| `GeneratedAt` | `DateTimeOffset` | |

### `ProductSearchResult` — [`ProductSearchResult.cs`](../../src/Daleel.Core/Models/ProductSearchResult.cs)
A directory of concrete, actionable results.

| Property | Type | Notes |
|---|---|---|
| `Query` / `Geo` / `Country` / `Summary` | `string` | |
| `Models` | `IReadOnlyList<ProductModel>` | **primary unit** — a model shown once with all offers |
| `IncludeInternational` | `bool` | |
| `Listings` | `IReadOnlyList<ProductListing>` | raw un-aggregated (compat/debug) |
| `Brands` | `IReadOnlyList<BrandInfo>` | manufacturers (not stores) |
| `Stores` | `IReadOnlyList<StoreInfo>` | retailers selling the product |
| `Reviews` | `IReadOnlyList<ReviewSource>` | editorial articles |
| `Social` | `SocialProof?` | aggregated forum/social opinion |
| `Marketplaces` | `IReadOnlyList<MarketplaceLink>` | category pages |
| `Comparisons` | `IReadOnlyList<ComparisonGroup>` | budget/mid/premium tiers |
| `Schema` | `ProductSchema` | product-type-aware compare columns |
| `GeneratedAt` | `DateTimeOffset?` | |

Plus computed counts (`ProductCount`, `StoreCount`, `BrandCount`, `ArticleCount`, `PlaceReviewCount`,
`UserReviewCount`).

### `ProductModel` — [`ProductModel.cs`](../../src/Daleel.Core/Models/ProductModel.cs)
A distinct product model with everything known about it; the unit the grid + detail panel render.

| Property | Type | Notes |
|---|---|---|
| `Name` / `Brand` / `Model` / `ProductLine` / `ImageUrl` | `string(?)` | |
| `Id` (computed) | `string` | `StableId.ForProduct(Brand, Model, Name)` — products have no DB key, so routing uses a deterministic hash |
| `Specs` | `IReadOnlyDictionary<string,string>` | merged spec sheet |
| `Msrp` / `MsrpCurrency` | `decimal?`/`string?` | |
| `Pros` / `Cons` / `ReviewSummary` | lists / `string?` | LLM-distilled |
| `BrandReputation` | `BrandReputation?` | |
| `Offers` | `IReadOnlyList<PriceOffer>` | every source, cheapest-first |

`PriceOffer` (same file): `Source`, `SourceType`, `Price`, `Currency`, `Url`, `Condition`,
`Availability`, `Seller`, `OriginalPrice`, `IsLocal`, `FreeShipping`, `IsLowest`; computed `IsDeal`,
`Tags` (`LOWEST`/`SALE`/`FREE SHIPPING`).

### `ProductListing` — [`ProductSearchResult.cs`](../../src/Daleel.Core/Models/ProductSearchResult.cs)
A single buyable listing: `Name`, `Brand`, `Model`, `Price`, `Currency`, `Url`, `ImageUrl`, `Source`,
`SourceType`, `Specs`, `Availability`, `Condition`, `OriginalPrice`, `Seller`; computed `AsMoney`,
`IsDeal`.

### `BrandInfo` — same file
A brand in the market: `Name`, `Url`, `LogoUrl`, `DbId?`, computed `Id` (DB id when persisted else
`StableId.ForBrand`), `ListingCount`, `Models` (sample names), `PriceFrom`/`PriceTo`/`PriceRange`,
`Reputation`.

### `StoreInfo` — same file
A retailer: `Name`, `Url`, `DbId?`, computed `Id` (`StableId.ForStore` fallback), `Address`, `Phone`,
`IsOnline`, `Latitude`/`Longitude` (computed `HasLocation`), `Rating`, `ReviewCount`, `Reviews`
(Google-Places star reviews).

Also in the file: `ReviewSource`, `MarketplaceLink`, `ComparisonGroup`.

---

## 6. Caching

A two-layer, time-bounded cache sits in front of the (paid) search pipeline.

### The store — [`PostgresCacheStore`](../../src/Daleel.Web/Data/PostgresCacheStore.cs)
Implements [`ICacheStore`](../../src/Daleel.Core/Caching/ICacheStore.cs), backed by the `SearchCache`
table. Registered as a **singleton** that opens a **fresh DbContext scope per call** (the agent runs
providers in parallel; a scoped `DaleelDbContext` isn't safe for concurrent use). `GetAsync` filters
`ExpiresAt > now` (expired ⇒ treated as absent even before purge). `SetAsync` upserts and swallows the
unique-constraint violation from a concurrent writer (caching is best-effort). `PurgeExpiredAsync`
bulk-deletes via `ExecuteDeleteAsync`.

### Cache keys — [`CacheKey`](../../src/Daleel.Core/Caching/CacheKey.cs)
A key is `{layer}:{sha256}` over a canonical, normalized join of inputs, so cosmetic differences
(`"iPhone 15"` vs `" iphone  15 "`, Arabic orthographic variants) collapse to the same entry. Text is
normalized with `ArabicNormalizer` (NFC, diacritic strip, letter folding, whitespace collapse) + case
folding.

- **Provider layer** (`ForProvider`): one external provider's raw response, keyed on
  `provider | kind | query | country | language | location | maxResults`.
- **Result layer** (`ForResult`): a whole normalized report, keyed on `query | geo | language`.

The cached **result** payload is a `CachedSearchResult` (`ResultJson`, `ResultType`, `FilteredCount`,
`FilteredCategories`) — see `CacheResultsActivity`.

### TTL & cleanup
TTL is **30 days** for both layers (`CacheTtl = TimeSpan.FromDays(30)` in
[`WorkflowSearchRunner`](../../src/Daleel.Web/Conversation/WorkflowSearchRunner.cs), threaded into the
pipeline state and passed to `SetAsync`). The
[`CacheCleanupService`](../../src/Daleel.Web/Services/CacheCleanupService.cs) `BackgroundService` sweeps
expired rows **once at startup, then weekly**, best-effort.

### Smart cache validation
A cache hit is **no longer automatically served** — its completeness is scored first.
[`CacheQualityValidator`](../../src/Daleel.Web/Pipeline/CacheQualityValidator.cs) (singleton,
`ICacheQualityValidator`) evaluates an `AgentAnswer` and returns a `CacheQualityReport`:

- **Score 0–100**, a weighted blend of three dimensions, normalised by the dimensions actually present
  (a store-less product search isn't penalised for missing stores):
  - **Products (50%)** — per model: image 0.30, price 0.30, specs 0.25 (≥3 specs = full, ≥1 = half),
    model name 0.10, SKU 0.05. (`RichSpecThreshold = 3`, mirroring `ItemEnrichmentService`.)
  - **Brands (25%)** — logo 0.6, description 0.4.
  - **Stores (25%)** — location 0.5, contact 0.3, Google-Maps data 0.2.
- **`CacheGap`** `[Flags]` enum records which dimensions fell short; `Missing` is a human-readable list;
  `ThinProducts`/`DeficientBrands`/`DeficientStores` are the concrete re-enrichment targets.
- **`CacheDecision`** (thresholds `ServeThreshold = 80`, `MissThreshold = 30`):
  - `≥ 80` → **`ServeAsIs`** (replay verbatim).
  - `< 30` → **`Miss`** (discard, run full live search).
  - mid-band → **`ServeAndEnrich`** if there are actionable targets, else `ServeAsIs` (re-enriching
    nothing would only burn a background pass).
- Non-product answers and empty results return `CacheQualityReport.Complete` (score 100).

**Wiring** ([`CheckCacheActivity`](../../src/Daleel.Web/Pipeline/SearchActivities.cs) ~L60): on a hit
it scores the payload; `Miss` falls through (downstream activities all run because `FromCache` stays
false); `ServeAsIs`/`ServeAndEnrich` set `FromCache = true` and stash `CacheQuality`. For
`ServeAndEnrich` the runner reads `CacheQuality` back and kicks off a **background pass that re-scrapes
only the missing pieces**, then streams the refreshed result. Each outcome records a `cache.*` pipeline
event (`hit`/`miss`/`partial`/`stale`) with score/decision/missing metadata. A corrupt/unscoreable
payload defaults to `Complete` — better to replay than re-run over a scoring hiccup.

---

## 7. Database migrations & `EnsureDatabase`

EF Core code-first migrations, one history per context:

- **App DB** (`DaleelDbContext`): `Data/Migrations/` — `20260627160344_InitialCreate` + model snapshot.
- **Event store** (`EventStoreDbContext`): `Events/Migrations/` —
  `20260625125441_InitialEventStore` + snapshot.
- **Elsa instance store** (`ManagementElsaDbContext`): Elsa-shipped migrations from the NuGet package.

### The `EnsureDatabase` flow — [`Program.cs`](../../src/Daleel.Web/Program.cs) (~L512)
Called once at boot (`EnsureDatabase(app)` right after `builder.Build()`), inside a DI scope:

1. **App DB** — `db.Database.Migrate()`. `Migrate()` **creates the `daleel` database** on the Postgres
   server if it doesn't yet exist (the bundled compose only seeds `daleel_events`), then applies
   pending migrations. This is the only non-best-effort step — Postgres is required, so a failure here
   fails the host fast.
2. **Seed system config** — `ISystemConfigService.SeedDefaultsAsync()` (idempotent).
3. **Event store** — if the `IDbContextFactory<EventStoreDbContext>` is registered, `Migrate()` its
   `daleel_events` database. **Best-effort**: a transient failure degrades to dropping events, logged,
   never stops the app.
4. **Elsa instance store** — if the `IDbContextFactory<ManagementElsaDbContext>` is registered (only
   when Postgres is configured), `Migrate()` it explicitly. This is required because Elsa's
   auto-migration only runs under `UseWorkflowRuntime`, which Daleel doesn't use — without this step the
   first instance save hit `42P01: relation "Elsa.WorkflowInstances" does not exist`. **Best-effort.**

> Connection gotchas worth knowing: the app DB connection is built by `ResolveAppDatabase` (rewrites
> `Database=daleel`); the event store and Elsa use `Resolve` directly. Rotating `POSTGRES_PASSWORD`
> requires wiping the `daleel_postgres_data` volume — run the deploy with `reset_postgres=true` (see
> [memory: postgres-password-reset-procedure]). And note the SQLite→Postgres migration copied **schema,
> not rows** — historical "login failing" reports were actually lost accounts, not auth bugs (see
> [memory: login-failure-sqlite-pg-data-loss]).

### DbContext concurrency note
`DaleelDbContext` is registered **transient** (not scoped) precisely because Postgres strictly enforces
"one command per connection". A circuit-shared scoped context caused a production crash unmasked by
Postgres (`Npgsql "command already in progress"`); transient gives each consumer its own context. The
cache store and event store side-step this entirely by opening their own scopes / using a context
factory. See [memory: blazor-dbcontext-concurrency].
