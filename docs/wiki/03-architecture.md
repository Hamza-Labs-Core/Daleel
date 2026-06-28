# 03 — Architecture & Infrastructure

> Source-of-truth reference for how Daleel is built, wired, deployed, and secured.
> Everything here is drawn from the actual source (`src/Daleel.Web/Program.cs`,
> `Dockerfile`, `deploy/`, `.github/workflows/`, and the security/moderation classes) —
> not from intent. When the code and this doc disagree, the code wins; fix the doc.

---

## 1. Stack

| Layer | Technology | Notes |
|-------|-----------|-------|
| Runtime | **.NET 8** (`net8.0`) | Every project targets `net8.0`, `Nullable` + `ImplicitUsings` enabled. |
| Web UI | **Blazor Server — Interactive Server render mode ONLY** | No WebAssembly, no `InteractiveAuto`. Every routable component injects server-only services (`DaleelDbContext`, `IConversationService`, `IEventStore`, SignalR notifiers, `IAgentFactory`) and can only run inside the live server circuit. The WASM runtime was removed because its second renderer muddied the render-mode handshake (the *"No interop methods are registered for renderer 1"* circuit crash). |
| Component library | **MudBlazor 8.15.0** | Theme, dialogs, snackbars, popovers — registered via `AddMudServices()`. |
| Data access | **EF Core 8 + Npgsql 8** | `Npgsql.EntityFrameworkCore.PostgreSQL` 8.0.11. PostgreSQL-only — there is **no SQLite or in-memory fallback**. |
| Workflow engine | **Elsa 3.3.3** | `Elsa`, `Elsa.EntityFrameworkCore`, `Elsa.EntityFrameworkCore.PostgreSql` all `3.3.3`. The search pipeline is an in-process Elsa workflow of `CodeActivity` steps. (The v2 `Elsa.Core`/`Elsa.Activities` packages from the original brief no longer exist in v3 — the `Elsa` meta-package supplies the engine, runtime, and built-in activities.) |
| Identity | **ASP.NET Core Identity** (`Microsoft.AspNetCore.Identity.EntityFrameworkCore` 8.0.12) | Email + password, cookie auth. |
| Logging | **Serilog** (`Serilog.AspNetCore` 9.0.0) | Console (Information+) plus Warning+ as JSON Lines to Cloudflare R2, or local files when R2 is unset. |
| Object storage | **AWS S3 SDK** (`AWSSDK.S3` 3.7.415.23) | S3-compatible client pointed at Cloudflare R2. |

> The target framework is **.NET 8**, not 9. Some package *versions* (Serilog.AspNetCore 9.0.0) carry a "9" major but resolve correctly against the `net8.0` TFM — the version was pinned deliberately to avoid skew with the AWS/Serilog S3 sink stack.

---

## 2. Project structure

The solution (`Daleel.sln`) is a clean-architecture layering: dependencies point **inward**, the domain core has zero external dependencies.

| Project | Kind | Responsibility |
|---------|------|----------------|
| `src/Daleel.Core` | Class lib | Domain core. `Models`, `Moderation` (the halal `ContentFilter`), `Arabic` (orthographic normalizer/matcher), `Caching` (`ICacheStore`), `Llm` (abstractions), `Observability` (`IApiCallObserver`, `CostEstimator`), `Pricing`, `Geo`, `Analysis`, `Intelligence`, `Pipeline` abstractions. No outward references. |
| `src/Daleel.Search` | Class lib | Search providers (`SerpApiProvider`, `GooglePlacesProvider`, Cloudflare browser, etc.), result `Aggregation`, the **`Http/SsrfGuard`**, `Instrumentation`, `ScrapeRouter`, and the `Moderation` bridge (`SearchResultModeration`) that adapts the core filter to `SearchResult`. |
| `src/Daleel.Agent` | Class lib | The LLM orchestration layer — `AgentService` (partial: `.Gather`, `.Intelligence`, `.Products`), `PromptTemplates`, LLM clients. Market/product/brand research. |
| `src/Daleel.Apify` | Class lib | Apify REST client + actor input builders + post mappers for social-media scraping. |
| `src/Daleel.Pipeline` | Class lib | Pipeline building blocks — `DealScorer`, `OpinionExtractor`, `PostDeduplicator`, `PriceTracker`, `MonitoringPipeline`, `JsonlResultWriter`, `Extraction`. |
| `src/Daleel.Cli` | Console app | `System.CommandLine` entry point + a `Composition` root. The web's `AgentFactory` mirrors this composition under DI. |
| `src/Daleel.Web` | Blazor Server app | **The deployed application.** Hosts the UI, DI wiring, auth, the Elsa search pipeline, admin pages, conversation/SignalR backend, EF migrations, R2 storage, email. |
| `src/Daleel.Web.Client` | WASM lib | Largely vestigial — registered historically but **not used at runtime** (no WASM render mode). Kept as a project reference; its renderer is not mapped. |

**Tests** mirror the source: `Daleel.Core.Tests`, `Daleel.Search.Tests`, `Daleel.Agent.Tests`, `Daleel.Pipeline.Tests`, `Daleel.Web.Tests` (spins up Postgres via Testcontainers), and `Daleel.E2E.Tests` (Playwright).

`Daleel.Web` references `Daleel.Web.Client`, `Daleel.Core`, `Daleel.Search`, `Daleel.Apify`, `Daleel.Pipeline`, `Daleel.Agent`. `InternalsVisibleTo` exposes internals to the matching `*.Tests` projects.

---

## 3. Dependency injection (Program.cs)

`Program.cs` is the composition root. It opens by **forcing both DI validators on in every environment** — not just Development:

```csharp
builder.Host.UseDefaultServiceProvider(options =>
{
    options.ValidateScopes = true;   // throws if a scoped service is resolved from the root provider
    options.ValidateOnBuild = true;  // walks the whole graph at Build() and throws on the first captive dependency
});
```

Rationale: a singleton (or hosted/background service) capturing a scoped/transient `DaleelDbContext` compiles and passes locally, then surfaces in **production** as an opaque circuit crash. Npgsql forbids concurrent use of one context, so a captured/shared context faults the instant two operations overlap. Forcing the validators on turns a silent prod incident into a loud startup failure (the deploy health check never goes green).

### The `DaleelDbContext` lifetime decision (the keystone)

```csharp
builder.Services.AddDbContext<DaleelDbContext>(
    o => o.UseNpgsql(appConnection),
    contextLifetime: ServiceLifetime.Transient);   // NOT EF's default Scoped
```

In Blazor Server, **a DI scope spans the entire SignalR circuit**. A *scoped* `DaleelDbContext` would therefore be a single instance shared by every component and service in that circuit. EF Core / Npgsql allow only one operation per context at a time, so two independently-rendering components issuing overlapping queries throw `Npgsql.NpgsqlOperationInProgressException: A command is already in progress` — terminating the circuit on page load. (SQLite tolerated this before the Postgres migration; Npgsql enforces it strictly.) **Transient** gives each consumer its own context. This single decision propagates to every stateless repository below.

### Service registry

| Service(s) | Lifetime | Why |
|-----------|----------|-----|
| `DaleelDbContext` | **Transient** | See above — avoids circuit-shared-context collisions. |
| `EventStoreDbContext` (factory) | **Factory** (`AddDbContextFactory`) | Separate `daleel_events` DB; consumers create contexts on demand. |
| `IEventStore → PostgresEventStore` | **Singleton** | Append-only firehose; opens its own scope per write. |
| Data Protection key ring | n/a (configured) | Persisted to `data/keys` so auth cookies survive redeploys (see §4). |
| Identity (`AddIdentityCore<ApplicationUser>`) | framework | `+ EntityFrameworkStores`, `SignInManager`, `AdditionalUserClaimsPrincipalFactory`, default token providers. |
| `ISecurityStampValidator`, `ITwoFactorSecurityStampValidator` | **Scoped** | `AddIdentityCore` doesn't register these; added explicitly for the `OnValidatePrincipal` hook (SEC-1). |
| `AuthenticationStateProvider → IdentityRevalidatingAuthenticationStateProvider` | **Scoped** | Surfaces auth state to Server components + revalidates the security stamp. |
| `BrowserStore`, `LayoutState`, `ICurrentUser → CurrentUser` | **Scoped** | Per-circuit UI state; `CurrentUser` reads claims only, no DB. |
| `ISearchHistoryRepository`, `ISavedResultRepository`, `IQuotaService`, `IAnalyticsService`, `ISystemConfigService`, `IApiCallLogRepository`, `IFilteredContentLogRepository` | **Transient** | Stateless wrappers over the (transient) `DaleelDbContext`. Transient prevents the always-present layout credits badge (`QuotaBadge → IQuotaService`) from sharing one context with whatever page is open. |
| `IBrandRepository`, `IStoreRepository`, `IProductProfileRepository`, `IBrandModelRepository`, `IScrapedPriceRepository`, `IVisionMatchCacheRepository` | **Transient** | Same circuit-concurrency reason. |
| `IR2StorageService` | **Singleton** | `R2StorageService` when R2 fully configured (uses an **SSRF-guarded** `HttpClient` for image copies), else `NullR2StorageService`. |
| `EmailNotificationOptions` | **Singleton** | Holds `APP_BASE_URL`. |
| `IEmailService` | **Singleton** | `ResendEmailService` when `RESEND_API_KEY` set, else `NullEmailService`. |
| `IUserEmailPreferences`, `SearchResultEmailTemplate`, `ISearchEmailNotifier` | **Scoped** | Email composition per circuit. |
| `IItemEnrichmentService` | **Scoped** | Background item enrichment. |
| `ProfileOptions` | **Singleton** | |
| `IProfileResearcher → ContextDevProfileResearcher` | **Singleton** | Researches brand/store profiles via Context.dev + LLM. |
| `IBrandProfileService`, `IStoreProfileService`, `IBrandCatalogService` | **Scoped** | |
| `ProfileRefreshService` | **HostedService** | Refreshes profiles older than 30 days. |
| `ISpecMerger` | **Singleton** | Stateless spec merge (raw → clean → canonical). |
| `IBrandCatalogSearcher`, `IProductIdentifier → SmartProductIdentifier` | **Scoped** | Product identification pipeline. |
| `IVisionMatcher` | **Singleton** | `VisionMatcher` when an `OPENROUTER_API_KEY` resolves (uses `OPENROUTER_VISION_MODEL`), else `NullVisionMatcher` (text-only fallback). |
| SignalR | framework | `AddSignalR()`. |
| `ISearchJobQueue → SearchJobQueue` | **Singleton** | In-memory job queue. |
| `SignalRConversationBroadcaster` (+ `IConversationBroadcaster`, `IConversationNotifier`) | **Singleton** | One broadcaster registered three ways. |
| Elsa (`AddElsa`) | framework | Activities scanned from `SearchWorkflow`'s assembly; `UseWorkflowManagement` on Postgres **only when configured** (optional persistence, see §5/§persistence). |
| `SearchPipelineState`, `SearchPipelineServices`, sub-workflow states (`BrandResearchState`, `StoreResearchState`, `ItemDeepDiveState`, `SubWorkflowServices`) | **Scoped** | Per-run state; each dispatched child gets its own scope. |
| `ISearchRunner → WorkflowSearchRunner` | **Scoped** | Drives the Elsa workflow per search. |
| `ICacheQualityValidator → CacheQualityValidator` | **Singleton** | Stateless; scores cache-hit completeness. |
| `IConversationStore`, `IConversationService` | **Transient** | Injected into the interactive Home component + the background worker; must not share a circuit-scoped context. |
| `SearchJobService` | **HostedService** | Background worker that drains the queue and runs searches off-request. |
| `ICacheStore → PostgresCacheStore` | **Singleton** | Opens its own DbContext scope per call (providers run in parallel). |
| `CacheCleanupService` | **HostedService** | Weekly sweep of expired cache entries. |
| `IMemoryCache`, `IIpRateLimiter → IpRateLimiter` | **Singleton** | In-memory fixed-window IP rate limiting (no Redis at this scale). |
| `IAgentFactory → AgentFactory` | **Singleton** | Builds fully-wired `AgentService` instances; resolves keys (user key → env). |
| `IProductDetailDbService` | **Transient** | Assembles product detail pages from saved DB rows. |
| `MonitorService` | **Singleton** | |
| `HttpClient` factory, `IStatusService` | Singleton/Transient | `/status` page provider probes. |
| `IProviderDiagnostics → ProviderDiagnostics` | **Scoped** | QA-only raw provider diagnostics, gated by `DIAGNOSTICS_ENABLED`. |
| Health checks | framework | `/health` liveness probe. |
| `ForwardedHeadersOptions` | configured | Honour `X-Forwarded-For`/`-Proto` from Caddy (`KnownNetworks`/`KnownProxies` cleared). |

**Optional-capability pattern:** R2, email, the event store, and Elsa persistence all register the *real* service when configured and a `Null*` no-op otherwise — the app degrades gracefully instead of failing to boot.

---

## 4. Authentication

**ASP.NET Core Identity, cookie-based, email + password.** No OAuth.

- **Schemes:** `AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies()`.
- **Cookie write path:** raw `/auth/login` and `/auth/register` POST endpoints (`MapAuthEndpoints`) — a real HTTP request, never a streaming Blazor circuit — so the `Set-Cookie` header always succeeds.
- **Cookie settings** (`ConfigureApplicationCookie`):
  - Name `Daleel.Auth`, `HttpOnly`, `SameSite=Lax`, `SecurePolicy=SameAsRequest` (Secure in prod behind Caddy's HTTPS; still works over plain-http local dev).
  - `ExpireTimeSpan = 30 days`, `SlidingExpiration = true`, paired with `isPersistent: true` at sign-in.
  - `LoginPath`/`AccessDeniedPath = /login`. API/hub paths (`/api`, `/hubs`) get a clean **401/403** instead of an HTML redirect (`ApiAwareRedirect`).
- **Antiforgery cookie:** `Daleel.Antiforgery`, `SameSite=Lax` (framework default Strict broke iOS Safari login POSTs arriving from external contexts → blank HTTP 400), `SecurePolicy=SameAsRequest`, `HttpOnly`.
- **Password policy:** length ≥ 8, requires digit + lowercase + uppercase; non-alphanumeric **not** required. `RequireUniqueEmail = true`; `RequireConfirmedAccount = false` (no email-confirmation step yet).
- **Security-stamp revalidation (SEC-1):** `OnValidatePrincipal = SecurityStampValidator.ValidatePrincipalAsync`, with validators registered manually (`AddIdentityCore` omits them) and `ValidationInterval = 5 min`. A disabled user's cookie or a revoked admin's baked-in role claim stops authenticating against `/api/*` within minutes (stamp rotation via `UpdateSecurityStampAsync`), rather than surviving until the 30-day cookie expires.

### Admin role

There is no Identity *roles table*; admin is a **bool on `ApplicationUser` (`IsAdmin`)** promoted to a `ClaimTypes.Role = "Admin"` claim by `AdditionalUserClaimsPrincipalFactory` (which also adds `display_name` and `avatar_url` claims so the UI renders without a DB hit).

Admin bootstrap (in `AuthEndpoints` registration):
- **Secure path:** an explicit comma-separated `DALEEL_ADMIN_EMAILS` allowlist — only those addresses become admin, and implicit promotion is **off**. On a fresh internet-facing deploy a random first registrant cannot seize the instance.
- **Convenience path (no allowlist):** the very **first** account to register is promoted (`!await userManager.Users.AsNoTracking().AnyAsync()`, checked before creation so a race can't mint two admins).

> Operational note (from prior incidents): if accounts appear "lost" on prod, it's usually data migration loss (schema copied, rows not), not an auth-code bug. Recover by re-registering; admins are re-minted via `DALEEL_ADMIN_EMAILS`.

---

## 5. Deployment

**Docker + Caddy + PostgreSQL on a Hetzner VPS**, fully automated from GitHub Actions.

### Container image (`Dockerfile`)

- **Stage 1 (`build`)** — `mcr.microsoft.com/dotnet/sdk:8.0`. Copies the `.sln` + every `.csproj` first (so `dotnet restore` is cached independently of source), restores, builds the whole solution `-c Release`, then `dotnet publish src/Daleel.Web` to `/app/publish`.
- **Stage 2 (`final`)** — `mcr.microsoft.com/dotnet/aspnet:8.0`, slim runtime. Installs `curl` (for the healthcheck), creates a non-root `daleel` user (uid/gid 1001, `--no-create-home`), copies the publish output, pre-creates `/app/data` owned by `daleel`. Listens on `:8080`, `ASPNETCORE_ENVIRONMENT=Production`. `HEALTHCHECK` curls `http://localhost:8080/health`.

> Because the app user has **no `$HOME`**, the Data-Protection key ring would fall back to in-memory and mint new keys on every restart — silently signing out every user. `Program.cs` fixes this by persisting keys to `data/keys` (mapped to the `daleel_data` volume) and pinning `SetApplicationName("Daleel")`.

### Compose stack (`deploy/docker-compose.yml`, runs from `/opt/daleel`)

Three services on a bridge network `web`:
1. **`daleel`** — the app image (`ghcr.io/hamza-labs-core/daleel:latest`). `env_file: .env`. Volume `daleel_data:/app/data` (key ring + fallback logs). `depends_on: postgres (service_healthy)`. Healthcheck on `/health`.
2. **`postgres`** — `postgres:16-alpine`, **REQUIRED**. User `daleel`, `POSTGRES_PASSWORD` from `.env` (a missing/empty password fails the deploy — no insecure default), seeds `POSTGRES_DB=daleel_events`. Volume `postgres_data`. The app's own `daleel` database is created at first boot by EF `Migrate()`.
3. **`caddy`** — `caddy:2-alpine`, TLS terminator. Ports 80, 443, 443/udp (HTTP/3). Mounts the `Caddyfile` (read-only) + persisted `caddy_data` (certs — do not delete) + `caddy_config`. `depends_on: daleel (service_healthy)`.

### CI/CD (`.github/workflows/`)

- **`ci.yml`** (push to main + PRs): `build (-warnaserror)` → `test` → `docker build` (image built only on push to main, not PRs). `concurrency: cancel-in-progress`.
- **`deploy.yml`** — the production pipeline. Triggers on **push to main** (auto-deploy) or manual `workflow_dispatch` (with an optional `image_tag`). `concurrency: deploy-production, cancel-in-progress: true` so a newer push cancels a stalled run (a Testcontainers/Ryuk hang once jammed the queue for 6.5h). Jobs:
  1. **build-test** — restore, `build -warnaserror`, `dotnet test` with `TESTCONTAINERS_RYUK_DISABLED=true` and `--blame-hang-timeout 5m`. Hard `timeout-minutes: 20`.
  2. **docker** — Buildx, log in to GHCR, `metadata-action` tags (`latest`, ref tag, `sha`), build & push with GHA layer cache.
  3. **deploy** — gated by the `production` GitHub Environment (manual-approval reviewers). Steps:
     - **Validate required secrets** — fails fast if any of `OPENROUTER_API_KEY`, `SERPAPI_KEY`, `CONTEXT_DEV_API_KEY`, `GOOGLE_PLACES_API_KEY`, `APIFY_TOKEN`, `POSTGRES_PASSWORD`, `DEPLOY_SSH_{HOST,USER,KEY}` is unset/empty/`CHANGE_ME`. Secrets are mapped to env vars (not interpolated into the script) so multi-line values and metacharacters are safe. Warns (not fails) if R2 creds are set without an images public host.
     - **Bootstrap VPS** (idempotent, `appleboy/ssh-action`) — installs Docker Engine + Compose, creates `/opt/daleel`, installs a `daleel.service` systemd unit (`docker compose up -d --wait`, survives reboots), opens UFW for 22/80/443 (+443/udp), configures Docker log rotation + logrotate, `docker login ghcr.io`. Every section guards on end-state so re-runs are fast no-ops. Replaces the old manual `setup.sh`.
     - **Write `/opt/daleel/.env`** from current GitHub secrets (`umask 077`, quoted heredoc so values are written verbatim, installed `0600`). The Postgres connection string is **derived** from `POSTGRES_PASSWORD` here, not stored separately (a hand-maintained string drifted to `Username=postgres` and broke auth).
     - **scp** `Caddyfile` / `docker-compose.yml` / `deploy.sh` to the box, install them, run `./deploy.sh "$DALEEL_TAG"` (pull → restart → health-check → rollback). The normal path **never** touches `postgres_data`.
- **`reset-postgres.yml`** — standalone, approval-gated workflow to wipe the `postgres_data` volume (needed after a `POSTGRES_PASSWORD` rotation, since Postgres bakes the password into the volume on first init). Deliberately isolated from the normal deploy so an ordinary push can never destroy the event store.

---

## 6. Configuration

Config is layered: `appsettings.json` (base) → `appsettings.Development.json` (local) → **environment variables** (`.env` on the VPS, rendered from GitHub secrets). Base `appsettings.json` is minimal: log levels, `AllowedHosts: *`, and `DetailedErrors: false` (kept off in prod to avoid info disclosure — SEC-2; on in Development only).

### Environment variables (`deploy/.env.example` is the canonical list)

| Var | Required? | Purpose |
|-----|-----------|---------|
| `DALEEL_IMAGE` | optional | Image tag to run (pin/rollback). Default `ghcr.io/hamza-labs-core/daleel:latest`. |
| `OPENROUTER_API_KEY` | **required** | The single LLM backend (routes to Anthropic/OpenAI/etc.). |
| `SERPAPI_KEY` | **required** | Web/shopping search. |
| `CONTEXT_DEV_API_KEY` | **required** | Scraping + brand/store profile research. |
| `GOOGLE_PLACES_API_KEY` | **required** | Store locations. |
| `APIFY_TOKEN` | **required** | Social-media scraping actors. |
| `BING_SEARCH_KEY` | optional | Azure Bing Search v7. |
| `POSTGRES_PASSWORD` | **required** | Single source of truth for the DB password. |
| `POSTGRES_CONNECTION_STRING` | **required** (one of) | Npgsql keyword form. Derived from `POSTGRES_PASSWORD` at `.env` render time. |
| `DATABASE_URL` | alt | `postgres://…` URL form (managed Postgres). |
| `POSTGRES_APP_DATABASE` | optional | Override the main app DB name (default `daleel`). |
| `CLOUDFLARE_ACCOUNT_ID` / `CLOUDFLARE_API_TOKEN` | optional | Cloudflare browser-rendering provider + derives the R2 endpoint. |
| `R2_ACCESS_KEY` / `R2_SECRET_KEY` | optional | Enable R2 object storage. |
| `R2_ENDPOINT` | optional | Override; blank ⇒ derived from `CLOUDFLARE_ACCOUNT_ID`. |
| `R2_BUCKET_{LOGS,IMAGES,SPECS,DATA}` | optional | Default `daleel-{logs,images,specs,data}`. `R2_BUCKET_NAME` is a legacy fallback for the logs bucket. |
| `R2_PUBLIC_URL_{IMAGES,SPECS,DATA}` | optional | Public hosts. **Images require one to be served from R2** (blank ⇒ hot-link from source, because the S3 endpoint 403s a plain `<img>` GET). `R2_PUBLIC_URL` is a legacy fallback for images. |
| `RESEND_API_KEY` | optional | Enables search-completion emails (blank ⇒ no-op). |
| `EMAIL_FROM` | optional | Sender; blank ⇒ `noreply@daleel.hamzalabs.dev`. |
| `APP_BASE_URL` | optional | Public URL in email links; blank ⇒ `https://daleel.hamzalabs.dev`. |
| `CADDY_DOMAIN` / `CADDY_ACME_EMAIL` | configured | Caddy host + Let's Encrypt contact. |
| `CADDY_DNS_TOKEN` | optional | Only for wildcard TLS (DNS-01). |
| `DALEEL_ADMIN_EMAILS` | optional | Admin allowlist (see §4). |
| `DIAGNOSTICS_ENABLED` | optional | Gates QA-only provider diagnostics (off in prod). |
| `DataProtection:KeysDirectory` | optional | Default `data/keys`. |

**GitHub secrets** (consumed by `deploy.yml`): all of the required keys above, plus `DEPLOY_SSH_HOST`/`USER`/`KEY`, the `R2_*` family, and `CLOUDFLARE_ACCOUNT_ID`/`CLOUDFLARE_API_TOKEN`. **Secret validation** runs before any box mutation: unset/empty/`CHANGE_ME` placeholders abort the deploy with a clear error rather than producing a half-broken `.env` that only fails at runtime.

> **Provider keys are also resolvable per-request from the browser Settings page** (`AgentRequest.Keys`). `AgentFactory.Resolve` checks the user-supplied key first, then the server env var — letting a visitor bring their own keys.

---

## 7. External services

| Service | Role | Key | Degradation when absent |
|---------|------|-----|------------------------|
| **OpenRouter** | The single LLM backend (routes to Anthropic/OpenAI/etc.) — agent reasoning, extraction, vision matching. | `OPENROUTER_API_KEY` (+ `OPENROUTER_VISION_MODEL`) | Agent can't run (hard requirement); vision matcher → `NullVisionMatcher` (text-only). |
| **SerpAPI** | Web + shopping search results. | `SERPAPI_KEY` | Web-search capability off. |
| **Context.dev** | Page scraping + brand/store profile research. | `CONTEXT_DEV_API_KEY` | Scraper/profile research off. |
| **Google Places** | Store locations + venue data. | `GOOGLE_PLACES_API_KEY` | Places capability off. |
| **Apify** | Social-media scraping actors (e.g. Facebook). | `APIFY_TOKEN` | Social capability off. |
| **Cloudflare (Browser Rendering)** | Browser-based scraping edge. | `CLOUDFLARE_ACCOUNT_ID` + `CLOUDFLARE_API_TOKEN` | Provider unavailable; treated as absent. |
| **Resend** | Transactional email (search-completion summaries). | `RESEND_API_KEY` | `NullEmailService` drops mail (best-effort; never affects a search). |
| **Cloudflare R2** | S3-compatible object storage, one bucket per concern: logs / images / specs / data. | `R2_ACCESS_KEY` + `R2_SECRET_KEY` (+ endpoint/buckets/public hosts) | `NullR2StorageService`; logs go to `/app/data/logs`, images hot-linked from source. |

Provider availability is summarised at runtime by `IAgentFactory.Describe` → `ProviderStatus(Llm, WebSearch, Places, Scraper, Social)` and surfaced on the `/status` page.

---

## 8. Content Security Policy

The CSP is an **infrastructure-level header set by Caddy** (`deploy/Caddyfile`, the `(security_headers)` snippet imported by every site), not application C#. It's a second line of defence behind Blazor's automatic output encoding, because the app renders LLM- and scraper-derived content.

```
Content-Security-Policy:
  default-src 'self';
  img-src 'self' data: https:;
  script-src 'self' 'unsafe-inline' 'unsafe-eval'
             https://maps.googleapis.com https://maps.gstatic.com https://static.cloudflareinsights.com;
  style-src 'self' 'unsafe-inline' https://fonts.googleapis.com;
  font-src 'self' https://fonts.gstatic.com data:;
  connect-src 'self' https: wss: ws:;
  frame-ancestors 'none';
  base-uri 'self';
  form-action 'self'
```

| Directive | What's allowed & why |
|-----------|----------------------|
| `default-src 'self'` | Lock everything to same-origin unless overridden. |
| `img-src 'self' data: https:` | Inline data URIs + any HTTPS image host (product images come from arbitrary scraped/R2 hosts). |
| `script-src` `'unsafe-inline' 'unsafe-eval'` | **Required by Blazor/MudBlazor** (inline bootstrap scripts + the WASM-style runtime). Plus Google Maps (`maps.googleapis.com`, `maps.gstatic.com`) and Cloudflare Insights. |
| `style-src 'unsafe-inline'` + `fonts.googleapis.com` | MudBlazor injects inline styles; Google Fonts CSS. |
| `font-src` `fonts.gstatic.com` + `data:` | Google Fonts + inline font data. |
| `connect-src 'self' https: wss: ws:` | SignalR needs WebSockets (`wss:`/`ws:`); HTTPS for any XHR/fetch. |
| `frame-ancestors 'none'` | Anti-clickjacking — backed up by `X-Frame-Options: DENY`. |
| `base-uri 'self'`, `form-action 'self'` | Prevent base-tag hijacking and cross-origin form posts. |

The same snippet also sets `X-Frame-Options: DENY`, `X-Content-Type-Options: nosniff`, `Referrer-Policy: strict-origin-when-cross-origin`, and strips the `Server` header.

---

## 9. Halal content filter

`Daleel.Core/Moderation/ContentFilter.cs` — a **bilingual (Arabic + English)** halal moderator. It screens free text and result collections, removing anything that mentions a blocked category, and records every removal in an audit log (`AuditLog` / `AuditDetails`, `FilterAudit`) for the admin "Filtered content" page — **filtered content is never shown to users, only logged**.

### What it filters (haram *content* — what a result sells, shows, or promotes)

| Category | Min strictness | Examples (EN) |
|----------|----------------|---------------|
| alcohol | Moderate | beer, wine, whisky, vodka, liquor, brewery, pub, **bar**, spirits, cider… |
| pork | Moderate | pork, bacon, ham, lard, prosciutto, non-halal… |
| gambling | Moderate | casino, betting, lottery, poker, roulette, slots, bookmaker… |
| adult | Moderate | porn, xxx, nude, escort, erotic, sex shop… |
| drugs | Moderate | cannabis, cocaine, heroin, narcotics, hashish… |
| tobacco | **Strict** | cigarette, cigar, vape, shisha, hookah, nicotine… |

- **Strictness levels** (`FilterStrictness`): `Off` (admin escape hatch) · `Moderate` (the five core categories) · **`Strict` (default — Moderate + tobacco)**.
- **Matching:** English terms compile once into a word-boundaried, case-insensitive alternation (`\b(?:beer|wine|…)s?\b`) — so "bar" catches "City Bar"/"bars" but **not** "barber". Arabic terms are matched against the canonical `ArabicNormalizer` form, so hamza/alef/taa-marbuta orthographic variants all hit. Patterns/normalized terms are cached for the process (hot path).
- **Thread-safety:** one `ContentFilter` is shared by reference across up to 5 parallel sub-workflows; the audit lists are guarded by a lock and readers return snapshots.
- **Typed entry points:** `FilterDeals`, `FilterDealResults`, `FilterStores`, `FilterStoreResults`, `FilterSocialPosts`, plus the generic `FilterResults<T>` and the `SearchResultModeration.FilterSearchResults` bridge in `Daleel.Search` (Core can't reference Search without a cycle).

### What it deliberately does **not** filter

> The filter screens haram **content**, not a store's **business model**. A store's *financing model* (riba / interest-based installment plans, banks, retailers offering credit) is **never** screened at any strictness level. The user can still walk in and pay cash for a perfectly halal TV or fridge — filtering the store would wrongly hide a legitimate option over a payment method the customer need not use. There is **intentionally no "riba"/"banking" category** (a Strict-level one existed previously and was removed because it conflated financing with the halal status of what a store sells).

So `FilterStores` *does* remove a bar or liquor store (they sell haram content), but it does **not** remove a bank or an interest-offering electronics retailer.

---

## 10. SSRF guard

`Daleel.Search/Http/SsrfGuard.cs` is the **single source of truth** for "is this URL safe for the server to fetch?" It protects every path that fetches attacker-influenced URLs — scraped pages, LLM-extracted `image_url` fields, guessed homepage domains, and the R2 image copier.

**Defence in depth, two layers:**

1. **Pre-flight checks** (callers run before opening a socket, so an internal target degrades gracefully):
   - `IsSafePublicUrl(url)` — **DNS-free**, synchronous. Rejects non-`http(s)` schemes, IP literals in any blocked range, and obvious internal hostnames (`localhost`, `*.localhost`, `*.local`, `*.internal`). Used when a third-party scraper edge (Context.dev / Cloudflare Browser) will do the actual fetch, so no DNS round-trip is warranted.
   - `IsSafePublicUrlAsync(url)` — **resolves DNS** and returns true only if the host resolves to *exclusively* safe public addresses (rejects if *any* resolved address is blocked — a poisoned answer can mix a public decoy with an internal target). For fetches issued directly from this host. Never throws.

2. **Connect-time enforcement** (`ConnectAsync`, wired as `SocketsHttpHandler.ConnectCallback`) — re-resolves and validates the address at connect time **and on every redirect hop**, then **pins the connection to the validated IPs** (no second DNS lookup). This closes the **DNS-rebinding TOCTOU window** a pre-flight check alone cannot. `CreateGuardedClient()` builds an `HttpClient` with this callback **and `AllowAutoRedirect = false`**, leaving no path to an internal address. (This is the client injected into `R2StorageService` in `Program.cs`.)

**Blocked ranges** (`IsBlocked(IPAddress)`):

| Range | Why |
|-------|-----|
| `127.0.0.0/8`, `::1` | Loopback. |
| `10/8`, `172.16/12`, `192.168/16` | RFC1918 private. |
| `169.254.0.0/16`, `fe80::/10` | Link-local — **covers the cloud metadata endpoint `169.254.169.254`**. |
| `fc00::/7` (`fc..`/`fd..`) | IPv6 ULA. |
| `100.64.0.0/10` | CGNAT. |
| `0.0.0.0/8` | "This host." |
| `224/4` + `240/4` + `255.255.255.255` | Multicast / reserved / broadcast. |
| IPv4-mapped IPv6 (`::ffff:10.0.0.1`) | Unwrapped via `MapToIPv4()` so the IPv4 rules apply. |
| Unknown address family | Refused by default. |

When a connection is blocked, `ConnectAsync` throws `SsrfBlockedException`; callers that fetch attacker-influenced URLs treat it as "skip" (best-effort) and never propagate it to the user.

---

## Cross-references

- Search pipeline internals & profile architecture: `docs/SEARCH_WORKFLOW.md`
- Original layering overview: `docs/architecture.md`
