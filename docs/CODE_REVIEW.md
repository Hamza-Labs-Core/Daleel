# Daleel — Deep Code Review

**Date:** 2026-06-22
**Reviewer:** Automated deep review (line-by-line, all source files)
**Commit reviewed:** `5548b29`
**Scope:** Entire solution — Core, Agent, Search, Pipeline, Apify, Web, Web.Client, CLI, and all tests (~20k LOC across 8 source + 5 test projects).

## Verdict

The codebase is **well above average in quality.** Baseline state at review start:

- `dotnet build -warnaserror` → **0 warnings, 0 errors**
- `dotnet test` → **241 tests, all passing**
- No `TODO`/`FIXME`/`HACK`/`NotImplementedException` anywhere in `src`
- No empty `catch {}` blocks; no `async void`; no `FromSqlRaw`/string-concatenated SQL
- No hardcoded secrets or connection strings (all keys via env vars / browser BYO; `appsettings.json` ships empty placeholders)
- No `MarkupString`/raw-HTML rendering (MudBlazor auto-encodes → no stored/reflected XSS surface)
- Per-`userId` data isolation is consistently enforced **at the query level** in every repository, with dedicated isolation tests

The issues found are concentrated where an **authorization boundary is implied but not actually enforced**. **All issues are now fixed** — the 4 HIGH, all 8 MEDIUM, and all 6 LOW.

After fixes: **build clean (`-warnaserror`), 255 tests passing** (+4 monitor-isolation, +10 covering the MEDIUM/LOW fixes: shared sentiment scorer, system-config caching/eviction, per-user saved-result count).

---

## CRITICAL

None.

---

## HIGH — all fixed in this change

### H1. Disabling / de-admining a user did not terminate their active session ✅ FIXED
**File:** `src/Daleel.Web/Components/Pages/Admin/AdminUsers.razor`

`ToggleDisableAsync` / `ToggleAdminAsync` called `UserManager.UpdateAsync` to flip `IsDisabled` / `IsAdmin`, but **never rotated the security stamp.** The `IsDisabled` check only runs at sign-in (`AuthEndpoints.cs`), and the `Admin` role claim is baked into the auth cookie at sign-in (`AdditionalUserClaimsPrincipalFactory`). Consequently:

- A **disabled** user kept full access until their cookie expired or they manually signed out — "Account disabled" was cosmetic for anyone already logged in.
- A **de-admined** user kept their `Admin` role claim (and `/admin/*` access) until re-login.

**Fix:** call `await Users.UpdateSecurityStampAsync(row.User)` after each toggle. The `IdentityRevalidatingAuthenticationStateProvider` revalidates the stamp (≤30 min) and tears down live circuits; the cookie's stamp validation rejects subsequent requests. UI copy updated to "signed out everywhere."

### H2. `/monitor` was unauthenticated and bypassed the registration + quota gate ✅ FIXED
**Files:** `src/Daleel.Web/Components/Pages/Monitor.razor`, `src/Daleel.Web/Services/MonitorService.cs`

The Monitor page had no `[Authorize]` attribute, and its `AddMonitor` / `RunNow` handlers called `MonitorService` **directly** — they did not go through `AgentPageBase.RunAsync`, which is where the sign-in gate and per-user quota live. An **anonymous** visitor could therefore create unlimited monitors and trigger **paid Apify actor runs** (`RunOnceAsync`) with no account and no quota — a cost / DoS abuse vector.

**Fix:** added `@attribute [Authorize]` to the page, and each handler now requires a resolved `_userId`.

### H3. `MonitorService` leaked every user's monitors and matched posts across tenants ✅ FIXED
**Files:** `src/Daleel.Web/Services/MonitorService.cs`, `Monitor.razor`, `MonitorServiceTests.cs`

`MonitorService` is a process-wide **singleton** that stored all monitors and all matched social posts in two shared `List<>`s with **no owner key.** `Monitors` / `Feed` returned the *entire* global set, so every authenticated user saw — and could pause/delete/run — **every other user's** keyword monitors and the full matched-post feed. This is the one place the otherwise-rigorous per-`userId` isolation was missing.

**Fix:** added `UserId` to `MonitorDefinition` and `MonitorHit`; every method (`Add`/`Remove`/`Toggle`/`RunOnceAsync`/`MonitorsFor`/`FeedFor`) now takes and filters on `userId`, mirroring the repository isolation pattern. Added 4 isolation tests proving a second user cannot see, mutate, or run another's monitors.

### H4. Rate limiter trusted a spoofable `X-Forwarded-For` first hop ✅ FIXED
**File:** `src/Daleel.Web/RateLimiting/IpRateLimitMiddleware.cs`

`ClientIp` read the raw `X-Forwarded-For` header and used `Split(',')[0]` (the **left-most** entry). Behind a single proxy (Caddy), the left-most value is **client-controlled** — the proxy appends the real address *after* whatever the client sent. An attacker could send `X-Forwarded-For: <random>` on each request and rotate it to evade **every** per-IP limit, including the 5/hour search guard that exists specifically to cap cost.

**Fix:** use `context.Connection.RemoteIpAddress`, which `UseForwardedHeaders` (already first in the pipeline) resolves from the trusted hop (`ForwardLimit=1` → takes the right-most/real entry). This is both correct and unspoofable for the documented single-proxy deployment. Existing rate-limit unit tests (which exercise `IpRateLimiter` directly) are unaffected.

---

## MEDIUM — all fixed in this change

### M1. The entire `/admin/settings` panel is non-functional (dead config) ✅ FIXED
**Files:** `src/Daleel.Web/Data/SystemConfigService.cs`, `IpRateLimitMiddleware.cs`, `ConversationService.cs`, `ConversationEndpoints.cs`, `SaveResultButton.razor`, `Saved.razor`, `SavedResultRepository.cs`

The seeded `SystemConfig` keys were **written and read back by the admin panel only** — no other code consumed them, so editing them had no effect.

**Fix:** every key is now consumed by a live service.
- **Rate limits** (`ratelimit.page_per_minute`, `ratelimit.api_per_minute`, `ratelimit.search_per_hour`): `IpRateLimitMiddleware` now resolves each window's limit from `ISystemConfigService` per request (the hardcoded `static readonly RateRule`s became fallback constants). The rule *names* stay fixed so the limiter's buckets are stable across a limit change.
- **Saved-results cap** (`limit.saved_results_free`): `SaveResultButton` checks `ISavedResultRepository.CountForUserAsync` (new) against the configured cap before saving; admins and unlimited-plan users are exempt.
- **Feature flags**: `feature.export_enabled` gates the JSON export buttons on `/saved` (UI **and** the handler methods); `feature.api_access_enabled` gates the whole HTTP `/api/search*` surface (403 when off — off by default).
- **Per-plan default models** (`model.default_free`, `model.default_pro`): `ConversationService.SubmitAsync` resolves the default model id from config (by the user's plan) when the caller didn't pick one.
- To keep this off the DB on the hot path, `SystemConfigService` now caches the whole config table as one snapshot (`IMemoryCache`, 30s TTL, evicted on `SetAsync`). The cache is an optional ctor dependency so unit tests still work without it. While here, fixed a latent bug: `SeedDefaultsAsync` was adding the **shared static** `Defaults` instances to the `DbContext`, so a later `SetAsync` mutated the process-wide defaults — it now seeds copies.

### M2. `HttpClient`-per-search anti-pattern in providers and LLM clients ✅ FIXED
**Files:** `Daleel.Search/Http/SharedHttpHandler.cs` (new), `Daleel.Search/Providers/*`, `Daleel.Agent/Llm/*`

Each provider did `client ??= new HttpClient()` (none `IDisposable`), and `AgentFactory.Build()` runs once per search → several undisposed `HttpClient`s per search (socket exhaustion / stale DNS risk).

**Fix:** added a process-wide shared `SocketsHttpHandler` (`SharedHttpHandler.Instance`, with `PooledConnectionLifetime`/idle timeout) and routed every provider/LLM-client's default-client construction through `SharedHttpHandler.CreateClient()` (`new HttpClient(handler, disposeHandler: false)`). All clients now share one pooled connection set; DNS changes are picked up via the connection lifetime. This is the lightweight stand-in for `IHttpClientFactory` in a library constructed manually by the CLI/web composition roots. The injectable test-client path is unchanged.

### M3. Quota check-then-increment is not atomic; charge-before-enqueue ✅ FIXED
**Files:** `src/Daleel.Web/Data/QuotaService.cs`, `Conversation/ConversationService.cs`

`TryConsumeAsync` read `SearchesUsed`, evaluated `CanSearch`, then `++` and saved — two concurrent requests could both pass and over-consume. `ConversationService.SubmitAsync` also consumed quota *before* the job row was committed.

**Fix:** `TryConsumeAsync` now performs an atomic conditional increment via `ExecuteUpdateAsync` — `WHERE SearchesUsed < limit ... SET SearchesUsed = SearchesUsed + 1` — so the check and the increment are a single SQL statement; rows-affected `0` means the last slot was already taken (returns false). The tracked entity is `ReloadAsync`'d so a follow-up `GetStatusAsync` on the same scoped context is consistent. `SubmitAsync` now pre-checks quota, **commits the job row first**, then consumes (rolling back the job if it loses the race), so a persistence failure never charges the user.

### M4. Raw exception messages are surfaced to the client ✅ FIXED
**Files:** `Conversation/SearchJobService.cs`, `Components/Shared/AgentPageBase.cs`

**Fix:** both paths now log the full exception server-side (`ILogger` — newly injected into `AgentPageBase`) and surface a generic message to the user ("Search failed. Please try again." / "Something went wrong while running your search."). The raw message is still kept in the server-only `SearchJob.Error` column for operator diagnostics.

### M5. Content filter compiles regex on every call ✅ FIXED
**File:** `src/Daleel.Core/Moderation/ContentFilter.cs`

**Fix:** each `Category` now precompiles its English terms into a single word-boundaried alternation (`\b(?:beer|wine|…)s?\b`) with `RegexOptions.Compiled`, built once and cached on the record; Arabic terms are pre-normalized once. `MatchCategory` runs one compiled match per category per item instead of recompiling ~70 patterns each call. Semantics (optional trailing "s", word boundaries) are unchanged — verified by the existing `ContentFilterTests`.

### M6. Analytics aggregations materialize rows then group in memory ✅ FIXED
**File:** `src/Daleel.Web/Data/AnalyticsService.cs`

**Fix:** `SearchesByTypeAsync`, `GeoDistributionAsync`, and the dashboard top-queries/top-users paths now `GroupBy` + `Count` + `Take` **in the database** (the keys are plain string columns, which SQLite translates), so only the small aggregated result set is materialized. The two genuinely untranslatable paths stay in memory and are documented inline: `SearchesPerDayAsync` (groups by `DateTime.Date`) and `ModerationStatsAsync` (splits a comma-joined string).

### M7. Duplicated sentiment-scoring logic ✅ FIXED
**Files:** `src/Daleel.Core/Analysis/KeywordSentiment.cs` (new), `Daleel.Agent/AgentService.cs`, `Daleel.Web/Services/MonitorService.cs`

**Fix:** extracted one shared `KeywordSentiment.Score(text)` helper in `Daleel.Core` (cue words pre-normalized once); both `AgentService` and `MonitorService` call it, so they now score identically (the two copies had drifted). Covered by new `KeywordSentimentTests`.

### M8. CSRF antiforgery disabled on state-changing API endpoints ✅ FIXED
**File:** `src/Daleel.Web/Conversation/ConversationEndpoints.cs`

**Fix:** documented the defense explicitly in code — the JSON `/api/search*` endpoints are protected against CSRF by (1) the `SameSite=Lax` Identity cookie (a cross-site POST omits it → rejected as unauthenticated) and (2) the required `application/json` content-type forcing a CORS preflight. The whole HTTP API is additionally gated behind `feature.api_access_enabled` (off by default, see M1), so it isn't even reachable cross-site unless an admin opts in.

---

## LOW — all fixed in this change

- **L1.** ✅ FIXED — added `@attribute [Authorize]` to `History.razor` and `Saved.razor`, for consistency with `Account.razor`.
- **L2.** ✅ FIXED — `/auth/logout` is now `MapPost` (was `GET`), which defeats the forced-logout `<img>`/GET CSRF vector. A new static-SSR `/logout` page submits the POST via an antiforgery-tokened form with an explicit "Sign out" button; `UserMenu`/`Account` link to it.
- **L3.** ✅ FIXED — `AuthEndpoints.Local(returnUrl)` now explicitly rejects protocol-relative (`//`) and backslash (`/\`) forms in addition to requiring a leading `/`, so it can't become an open redirect.
- **L4.** ✅ FIXED — `AgentService` is now a `partial class`; the Gather half moved to `AgentService.Gather.cs`, bringing each file comfortably under the 500-line smell threshold.
- **L5.** ✅ FIXED — `SearchJobQueue.RequestCancel` wraps `cts.Cancel()` in a `try/catch (ObjectDisposedException)` and treats the disposed-in-the-window case as a no-op (returns false).
- **L6.** ✅ FIXED — extracted `ChatCompletionParsing.ExtractContent` (shared by `OpenAiClient`/`OpenRouterClient`), which guards a missing/empty `choices` array and throws a clear `ProviderException` (surfacing the API's own error message when present) instead of an opaque index error.

---

## UI review notes (Blazor)

- **RTL / bilingual:** consistent Arabic-lead, English-secondary labels; `dir="@Catalog.Dir(...)"` applied per-string for mixed content. Good.
- **Theme tokens:** components use MudBlazor color tokens (`Color.Primary`, etc.); no hardcoded hex in components. No raw-HTML injection.
- **Loading/empty/error states:** present across agent pages (`Busy` spinner, `NoRecordsContent`, `MudAlert` errors, empty-state cards on Monitor/History/Home).
- **Auth on pages:** Admin pages correctly gated with `[Authorize(Roles="Admin")]`; `Account` with `[Authorize]`. Agent feature pages enforce sign-in + quota at the **action** layer via `AgentPageBase.RunAsync` (validated server-side, including the admin-only "filter Off" escape hatch). `Monitor` now `[Authorize]`-gated (H2).
- **Streaming conversation page (`Home.razor`):** uses `@rendermode InteractiveServer` with an in-process `IConversationNotifier` (not a JS SignalR client); reconnect is handled by the **Blazor circuit**, and state is rehydrated from `IConversationStore` on (re)connect — so a dropped connection recovers cleanly. Event handlers are correctly unsubscribed in `Dispose()`. The SignalR `ConversationHub` (for external clients) is `[Authorize]` and groups by user id.

## Tests

Real, behavior-asserting tests (no `true==true`). Critical paths covered: data isolation (`UserDataIsolationTests`), quota (`QuotaServiceTests`), rate limiting, content filter, Arabic normalization/matching, providers (stub HTTP handler), conversation backend, and component render smoke tests. Coverage gap closed by this change: **monitor cross-user isolation** (4 new tests).

---

## Summary table

| ID | Severity | Area | Status |
|----|----------|------|--------|
| H1 | HIGH | Session not terminated on disable/de-admin | ✅ Fixed |
| H2 | HIGH | `/monitor` unauthenticated + bypasses quota | ✅ Fixed |
| H3 | HIGH | MonitorService cross-user data leak | ✅ Fixed |
| H4 | HIGH | Rate limiter trusts spoofable XFF | ✅ Fixed |
| M1 | MED | `/admin/settings` config never consumed | ✅ Fixed |
| M2 | MED | HttpClient-per-search (no pooling/dispose) | ✅ Fixed |
| M3 | MED | Quota check/increment not atomic | ✅ Fixed |
| M4 | MED | Raw exception messages to client | ✅ Fixed |
| M5 | MED | Content-filter regex recompiled per call | ✅ Fixed |
| M6 | MED | Analytics group-by in memory | ✅ Fixed |
| M7 | MED | Duplicated sentiment scorer | ✅ Fixed |
| M8 | MED | CSRF antiforgery disabled on /api/search | ✅ Fixed |
| L1 | LOW | History/Saved missing `[Authorize]` | ✅ Fixed |
| L2 | LOW | `/auth/logout` GET (forced-logout CSRF) | ✅ Fixed |
| L3 | LOW | `Local()` open-redirect (`//`, `/\`) | ✅ Fixed |
| L4 | LOW | `AgentService.cs` >500 lines | ✅ Fixed |
| L5 | LOW | Cancel/dispose race in `SearchJobQueue` | ✅ Fixed |
| L6 | LOW | `choices[0]` unguarded in LLM clients | ✅ Fixed |
