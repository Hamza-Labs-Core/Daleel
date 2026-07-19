using Daleel.Web.Auth;
using Daleel.Web.Components;
using Daleel.Web.Conversation;
using Daleel.Web.Data;
using Daleel.Web.Logging;
using Daleel.Web.RateLimiting;
using Daleel.Web.Services;
using Elsa.EntityFrameworkCore.Extensions;
using Elsa.EntityFrameworkCore.Modules.Management;
using Elsa.Extensions;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

// ── DI lifetime safety: fail fast on captive dependencies in EVERY environment ─
// By default the host enables both DI validators ONLY in Development, so a lifetime mistake — a
// singleton (or a hosted/background service, which the container treats as a singleton) capturing a
// scoped/transient service such as DaleelDbContext — compiles, passes locally, and then surfaces in
// PRODUCTION as an opaque runtime circuit crash rather than a startup error. After the SQLite→Postgres
// migration this class of bug is especially dangerous: Npgsql strictly forbids concurrent use of a
// single context, so a captured/shared context faults the moment two operations overlap. Forcing both
// checks ON everywhere makes the container refuse to boot on any such mistake — turning a silent prod
// incident into a loud, pre-deploy failure (the deploy health check never goes green).
//   • ValidateScopes  — throws if a scoped service is resolved from the ROOT provider at runtime
//                       (i.e. a singleton/background service reaching for a scoped service).
//   • ValidateOnBuild — walks the entire service graph at builder.Build() and throws on the FIRST
//                       captive dependency, so the failure is caught at startup, not on first request.
// Safe to force on: WebApplication.CreateBuilder already enables both in Development, and the app boots
// there with the same config-driven graph, so this only changes which environments enforce the rule.
builder.Host.UseDefaultServiceProvider(options =>
{
    options.ValidateScopes = true;
    options.ValidateOnBuild = true;
});

// ── Logging (Serilog) ─────────────────────────────────────────────────────────
// Replace the default console logger with Serilog: Information+ to the console for debugging, and
// Warning and above as JSON Lines to Cloudflare R2 (when R2_* env vars are set) or to local files
// under /app/data/logs otherwise. See Daleel.Web.Logging.SerilogConfiguration.
builder.AddDaleelLogging();

// Razor components, Server-interactive ONLY. Every routable component injects server-only services
// (DaleelDbContext, IConversationService, IEventStore, the SignalR notifiers, IAgentFactory) and so can
// only run inside the live server circuit — none is InteractiveAuto/InteractiveWebAssembly. The
// WebAssembly runtime was registered but unused; its second renderer only muddied the render-mode
// handshake behind the "No interop methods are registered for renderer 1" circuit crash.
//
// DetailedErrors surfaces the full server-side stack trace of any unhandled circuit exception to the
// browser (instead of the opaque "Unhandled exception on the current circuit"). It's config-driven so
// it can be toggled per environment: it defaults OFF in base appsettings.json (don't leak stack traces
// to clients) and is enabled only in appsettings.Development.json for local debugging. See SEC-2.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents(options =>
        // TEMPORARY (SEC-2): forced ON in ALL environments to surface the real production circuit
        // exception that the config-driven flag (false in base appsettings) was hiding. Revert to
        // `builder.Configuration.GetValue("DetailedErrors", false)` once the prod crash is diagnosed.
        options.DetailedErrors = true);

// MudBlazor UI services (theme, dialogs, snackbars, popovers).
builder.Services.AddMudServices();

// ── Localization (English + Arabic, cookie-based) ───────────────────────────
// No ResourcesPath: the SharedResource marker type already lives in the Daleel.Web.Resources
// namespace, so its translations embed as Daleel.Web.Resources.SharedResource[.ar]. Setting
// ResourcesPath="Resources" here would make the localizer look for a *doubled* prefix
// (Daleel.Web.Resources.Resources.SharedResource), miss every key, and render raw ids ("Nav.Home").
builder.Services.AddLocalization();
var supportedCultures = new[] { "en", "ar" };
builder.Services.Configure<Microsoft.AspNetCore.Builder.RequestLocalizationOptions>(o =>
{
    o.SetDefaultCulture("en")
     .AddSupportedCultures(supportedCultures)
     .AddSupportedUICultures(supportedCultures);
    // Cookie first (explicit user choice), then the browser's Accept-Language header.
    o.RequestCultureProviders.Insert(0, new Microsoft.AspNetCore.Localization.CookieRequestCultureProvider());
});

// ── Persistence (PostgreSQL) ─────────────────────────────────────────────────
// The whole app runs on PostgreSQL — there is no SQLite/in-memory fallback. The main app DB
// (Identity + every app entity) lives in its own `daleel` database; the pipeline event store and
// Elsa's workflow-instance store get their own `daleel_events` database on the SAME server, so the
// three migration histories never collide (see PostgresConnection.ResolveAppDatabase). A missing
// POSTGRES_CONNECTION_STRING / DATABASE_URL is a fatal misconfiguration: fail fast rather than boot a
// half-configured app with no database.
var appConnection = Daleel.Web.Events.PostgresConnection.ResolveAppDatabase(builder.Configuration)
    ?? throw new InvalidOperationException(
        "PostgreSQL is required but not configured. Set POSTGRES_CONNECTION_STRING (Npgsql keyword " +
        "form) or DATABASE_URL (postgres:// URL). See deploy/.env.example.");
// TRANSIENT context lifetime — deliberately NOT EF's default Scoped. In Blazor Server a DI scope spans
// the ENTIRE SignalR circuit, so a *scoped* DaleelDbContext is a single instance shared by every
// component and service in that circuit. EF Core / Npgsql allow only one operation per context (one
// connection) at a time, so two independently-rendering components issuing overlapping queries on that
// shared context throw `Npgsql.NpgsqlOperationInProgressException: A command is already in progress` —
// which terminates the circuit on page load. The authenticated home reproduced this exactly: the
// layout's credits widget (QuotaService → UserSubscriptions⋈SubscriptionPlans) and the page's email-
// prefs lookup (UserManager → AspNetUsers) raced on the one shared context. SQLite tolerated this before
// the Postgres migration; Npgsql enforces it strictly. Transient gives each consumer its OWN context, so
// no two ever share one — removing the collision without touching any repository.
//
// This is safe for the whole app, not just the circuit: no singleton injects DaleelDbContext (the search
// worker + sub-workflow dispatchers create their own scopes; PostgresCacheStore uses IServiceScopeFactory),
// every consumer that needs one context for a multi-step unit of work (e.g. SearchJobService.ProcessAsync,
// which tracks the SearchJob across Running→Completed) resolves it ONCE and reuses that instance, and no
// single consumer issues parallel queries on its own context. Identity's UserManager/UserStore likewise
// each capture their own context per scope.
builder.Services.AddDbContext<DaleelDbContext>(
    o => o.UseNpgsql(appConnection),
    contextLifetime: ServiceLifetime.Transient);

// ── Pipeline event store (PostgreSQL) ─────────────────────────────────────────
// A separate append-only context on the same Postgres server (the `daleel_events` database) records
// every pipeline action (provider call, scrape, LLM, cache, profile, places, extract) for the admin
// usage/cost dashboard. Kept in its own database so the high-volume event firehose stays off the
// transactional app DB.
var eventStoreConn = Daleel.Web.Events.PostgresConnection.Resolve(builder.Configuration);
builder.Services.AddDbContextFactory<Daleel.Web.Events.EventStoreDbContext>(
    o => o.UseNpgsql(eventStoreConn));
builder.Services.AddSingleton<Daleel.Web.Events.IEventStore, Daleel.Web.Events.PostgresEventStore>();
// The unified admin activity timeline shares the same daleel_events database + factory. It records the
// cross-system feed (search lifecycle, pipeline actions bridged from the firehose, logins, background
// sweeps, errors) the /admin/timeline page reads. Best-effort like the cost event store above.
builder.Services.AddSingleton<Daleel.Web.Events.ISystemEventLog, Daleel.Web.Events.PostgresSystemEventLog>();
// Live per-search event sink: batches SearchEvents (emitted anywhere in the pipeline via the ambient
// AmbientSearchEvents carrier) and flushes them to the timeline off the hot path, so each search's events
// appear live instead of only at end-of-run.
builder.Services.AddSingleton<Daleel.Web.Events.SystemEventWriter>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Daleel.Web.Events.SystemEventWriter>());
builder.Services.AddSingleton<Daleel.Web.Events.ISearchEventSinkFactory, Daleel.Web.Events.SystemEventSinkFactory>();

// ── Data Protection (auth-cookie encryption keys) ─────────────────────────────
// ASP.NET encrypts the Identity auth cookie with Data Protection keys. By default those keys live in
// $HOME/.aspnet/DataProtection-Keys, but the container runs as a --no-create-home user (see Dockerfile),
// so there is no $HOME and the key ring falls back to IN-MEMORY storage. New keys are then minted on every
// container start, which makes every previously-issued cookie undecryptable — i.e. every user is silently
// signed out on each redeploy/restart. Persisting the keys to the data/ directory fixes this: that path
// maps to the `daleel_data` named volume (owned by the app user) and survives redeploys. (The database
// itself now lives in Postgres; this volume only holds the Data-Protection key ring and local-fallback
// logs.) Path is configurable; defaults under data/ so container + local both work.
//   SetApplicationName pins the key-ring purpose string so the keys stay valid across app revisions.
var keysDirectory = builder.Configuration.GetValue<string>("DataProtection:KeysDirectory") ?? "data/keys";
Directory.CreateDirectory(keysDirectory);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keysDirectory))
    .SetApplicationName("Daleel");

// ── Identity (cookie auth, email + password) ──────────────────────────────────
// Standard Identity cookie schemes. The cookie is written by the raw /auth/login and /auth/register
// POST endpoints (see AuthEndpoints) — a real HTTP request, never a streaming Blazor circuit, so the
// Set-Cookie header write always succeeds.
builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme)
    .AddIdentityCookies();

// Unauthenticated visitors are sent to /login instead of a 404 when a [Authorize] page is hit —
// except API/hub calls, which get a clean 401/403 instead of an HTML redirect.
builder.Services.ConfigureApplicationCookie(o =>
{
    o.LoginPath = "/login";
    o.AccessDeniedPath = "/login";

    // Explicit cookie settings for life behind the Caddy reverse proxy.
    o.Cookie.Name = "Daleel.Auth";
    o.Cookie.HttpOnly = true;                            // never exposed to JS
    o.Cookie.SameSite = SameSiteMode.Lax;               // sent on top-level nav (the login 302) — same-site app, so Lax is enough; None is unnecessary and would force Secure everywhere
    // SameAsRequest, NOT Always: behind Caddy the forwarded scheme is https so the cookie is marked Secure
    // in production, but plain-http local dev still works (Always would make the browser drop it over http).
    o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    o.ExpireTimeSpan = TimeSpan.FromDays(30);            // long-lived; paired with isPersistent:true at sign-in
    o.SlidingExpiration = true;                          // active users don't get logged out at the 30-day mark

    o.Events.OnRedirectToLogin = ctx => ApiAwareRedirect(ctx, StatusCodes.Status401Unauthorized);
    o.Events.OnRedirectToAccessDenied = ctx => ApiAwareRedirect(ctx, StatusCodes.Status403Forbidden);

    // Revalidate the security stamp on every authenticated request (HTTP API included, not just the
    // Blazor circuit). Without this, a disabled user's cookie — or a revoked admin's baked-in role
    // claim — keeps authenticating against /api/* until the 30-day cookie expires. The validator
    // re-checks the cookie's stamp against the database at most once per SecurityStampValidatorOptions
    // .ValidationInterval (5 min, configured below), so disabling/role changes (which rotate the stamp
    // via UpdateSecurityStampAsync) take effect within minutes everywhere. See SEC-1.
    o.Events.OnValidatePrincipal = SecurityStampValidator.ValidatePrincipalAsync;
});

// Antiforgery cookie settings. The framework default is SameSite=Strict with no Secure flag, which
// iOS Safari withholds on the login form POST when the user arrived from an external context (tapping
// a link, a search result) — antiforgery then fails and /auth/login returns a blank HTTP 400, i.e. the
// "blank page / stuck on the login page on mobile" report. Lax (the same as the auth cookie) is still
// CSRF-safe — a cross-site POST never carries the cookie under Lax either — but is reliably sent on the
// same-site, top-level form submit. SameAsRequest marks it Secure over the proxied HTTPS in production.
builder.Services.AddAntiforgery(o =>
{
    o.Cookie.Name = "Daleel.Antiforgery";
    o.Cookie.SameSite = SameSiteMode.Lax;
    o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    o.Cookie.HttpOnly = true;
});

static Task ApiAwareRedirect(Microsoft.AspNetCore.Authentication.RedirectContext<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions> ctx, int statusCode)
{
    if (ctx.Request.Path.StartsWithSegments("/api") || ctx.Request.Path.StartsWithSegments("/hubs"))
    {
        ctx.Response.StatusCode = statusCode;
        return Task.CompletedTask;
    }

    ctx.Response.Redirect(ctx.RedirectUri);
    return Task.CompletedTask;
}

builder.Services.AddIdentityCore<ApplicationUser>(options =>
    {
        options.SignIn.RequireConfirmedAccount = false; // no email-confirmation step (yet)
        options.User.RequireUniqueEmail = true;         // email is the login identifier — must be unique
        // Password strength policy enforced by UserManager.CreateAsync on registration.
        options.Password.RequiredLength = 8;
        options.Password.RequireDigit = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireNonAlphanumeric = false; // length + mixed case is enough friction
    })
    .AddEntityFrameworkStores<DaleelDbContext>()
    .AddSignInManager()
    .AddClaimsPrincipalFactory<AdditionalUserClaimsPrincipalFactory>()
    .AddDefaultTokenProviders();

// Cookie security-stamp validation (SEC-1). AddIdentityCore — unlike AddIdentity — does not register
// the stamp validators, so the OnValidatePrincipal hook above has nothing to call unless we add them.
// A 5-minute interval bounds how long a revoked/disabled user's cookie can survive on the HTTP API.
builder.Services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.FromMinutes(5));
builder.Services.AddScoped<ISecurityStampValidator, SecurityStampValidator<ApplicationUser>>();
builder.Services.AddScoped<ITwoFactorSecurityStampValidator, TwoFactorSecurityStampValidator<ApplicationUser>>();

// Surfaces auth state to interactive Server components and revalidates against the security stamp.
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthorization();

// ── Daleel intelligence layer ─────────────────────────────────────────────────
builder.Services.AddScoped<BrowserStore>();              // localStorage bridge (per circuit)
builder.Services.AddScoped<LayoutState>();               // shared theme state
builder.Services.AddScoped<ICurrentUser, CurrentUser>(); // authenticated id, from the circuit (claims only, no DB)
// The data repositories/services below are TRANSIENT, not scoped. They are stateless wrappers over the
// (now transient) DaleelDbContext, and in Blazor Server a scoped service is ONE shared instance for the
// whole circuit. Two components that render concurrently — most importantly the always-present layout
// credits badge (QuotaBadge → IQuotaService) and whatever page is open — would then resolve the SAME
// scoped service, share its one DbContext, and collide ("a command is already in progress" / "a second
// operation was started on this context"), terminating the circuit on page load. Transient gives each
// component its own instance + its own context, so no two components ever share one. (Safe because these
// are stateless and no singleton injects them — a singleton capturing today's scoped repos would already
// fail DI scope validation at startup.) See the DaleelDbContext registration note above.
builder.Services.AddTransient<ISearchHistoryRepository, SearchHistoryRepository>();
builder.Services.AddTransient<ISavedResultRepository, SavedResultRepository>();
builder.Services.AddTransient<IQuotaService, QuotaService>();
builder.Services.AddTransient<IAnalyticsService, AnalyticsService>();
builder.Services.AddTransient<ISystemConfigService, SystemConfigService>();
builder.Services.AddTransient<IApiCallLogRepository, ApiCallLogRepository>();
builder.Services.AddTransient<IFilteredContentLogRepository, FilteredContentLogRepository>();
builder.Services.AddTransient<IRelevanceFlagRepository, RelevanceFlagRepository>();
builder.Services.AddTransient<IRelevanceFeedbackService, RelevanceFeedbackService>();
builder.Services.AddSingleton<IRelevancePolicyProvider, RelevancePolicyProvider>();
builder.Services.AddTransient<IModerationWhitelistRepository, FilteredContentLogRepository>();
builder.Services.AddTransient<IModerationRuleRepository, ModerationRuleRepository>();
builder.Services.AddTransient<IImageModerationLogRepository, ImageModerationLogRepository>();
builder.Services.AddTransient<IImageModerationRuleRepository, ImageModerationRuleRepository>();

// The dynamic feedback loop: an LLM auditor reviews persisted findings on an interval, stores
// auto-ratings (admin ratings always override), and self-activates keyword suppressions on
// repeated wrong-flag consensus. Inert when no LLM key is configured.
builder.Services.AddHostedService<Daleel.Web.Moderation.FindingAutoReviewService>();
builder.Services.AddHostedService<Daleel.Web.Moderation.ImageReEvalService>();

// The moderation feedback loop's read side: whitelist keys + rating-tuned thresholds, briefly
// cached and handed to every search run. Singleton (scope-factory based) so background jobs share
// the snapshot cache.
builder.Services.AddSingleton<Daleel.Web.Moderation.IModerationPolicyProvider,
    Daleel.Web.Moderation.ModerationPolicyProvider>();
// Zero-cost haram-consumable query pre-screen (blocks "beer" etc. at submission before any spend).
builder.Services.AddSingleton<Daleel.Web.Moderation.IQueryPreScreen, Daleel.Web.Moderation.QueryPreScreen>();

// The model BOTH vision screens below run on, resolved per call: the admin's model.vision row at
// /admin/settings → DALEEL_MODERATION_VISION_MODEL (the pre-setting bootstrap) → each screen's own
// default. The screens are singletons, so this indirection is what lets an admin switch the vision
// model without a redeploy.
builder.Services.AddSingleton<Daleel.Web.Moderation.IVisionModelResolver>(sp =>
    new Daleel.Web.Moderation.VisionModelResolver(sp.GetRequiredService<IServiceScopeFactory>()));

// Vision screening of individual result images (halal moderation). OpenRouter-only, like the
// product-identification matcher; inert when no key is configured.
builder.Services.AddSingleton<Daleel.Core.Moderation.IHalalImageClassifier>(sp =>
{
    var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
    if (string.IsNullOrWhiteSpace(key))
    {
        return new Daleel.Core.Moderation.NullHalalImageClassifier();
    }

    return new Daleel.Web.Moderation.OpenRouterImageHalalClassifier(
        key.Trim(),
        sp.GetRequiredService<Daleel.Web.Moderation.IVisionModelResolver>(),
        sp.GetRequiredService<ILogger<Daleel.Web.Moderation.OpenRouterImageHalalClassifier>>(),
        sp.GetRequiredService<Daleel.Core.Caching.ICacheStore>(),
        scopeFactory: sp.GetRequiredService<IServiceScopeFactory>());
});

// Product-shot vision screen: keeps only clean product photos on cards (rejects lifestyle/room scenes,
// promo banners, logos). OpenRouter-only, cached per image URL, fail-open; inert without a key.
builder.Services.AddSingleton<Daleel.Web.Moderation.IProductImageScreen>(sp =>
{
    var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
    if (string.IsNullOrWhiteSpace(key))
    {
        return new Daleel.Web.Moderation.NullProductImageScreen();
    }

    return new Daleel.Web.Moderation.OpenRouterProductImageScreen(
        key.Trim(),
        sp.GetRequiredService<Daleel.Web.Moderation.IVisionModelResolver>(),
        sp.GetRequiredService<ILogger<Daleel.Web.Moderation.OpenRouterProductImageScreen>>(),
        sp.GetRequiredService<Daleel.Core.Caching.ICacheStore>());
});
builder.Services.AddHttpContextAccessor();

// Persistent brand/store profiles: researched once via Context.dev + the LLM, saved to Postgres, and
// refreshed only when stale (>30 days). Search results JOIN against these instead of re-fetching.
// Transient for the same Blazor-circuit concurrency reason as the repositories above.
builder.Services.AddTransient<IBrandRepository, BrandRepository>();
builder.Services.AddTransient<IStoreRepository, StoreRepository>();
// Learned per-domain store-search interfaces (SiteSearchCandidates leads with the winning template).
builder.Services.AddTransient<ISiteSearchProfileRepository, SiteSearchProfileRepository>();
builder.Services.AddTransient<IProductProfileRepository, ProductProfileRepository>();
builder.Services.AddTransient<IBrandModelRepository, BrandModelRepository>();
builder.Services.AddTransient<IBrandSiteRepository, BrandSiteRepository>();
builder.Services.AddTransient<IScrapedPriceRepository, ScrapedPriceRepository>();
// Entity-document persistence: Postgres index row + R2 daleel-data JSON document (the source of truth)
// for products/services/places. Transient so each save gets its own DbContext (same circuit-safety rule
// as the profile repositories above).
builder.Services.AddTransient<IEntityRecordRepository, EntityRecordRepository>();
builder.Services.AddTransient<Daleel.Web.Persistence.ISearchEntityStore, Daleel.Web.Persistence.SearchEntityStore>();

// Real-time translation (DeepL) with a permanent Postgres cache. Optional: when DEEPL_API_KEY is unset the
// service reports Enabled=false and every <TranslatedText> is a transparent pass-through, so the app runs
// unchanged without DeepL. Options + the stateless HTTP client are singletons; the repository and the
// service are Transient so each UI component that translates gets its OWN DbContext — concurrent
// translations from independent components must never share one Npgsql connection (the same Blazor-circuit
// hazard the profile repositories above avoid).
builder.Services.AddSingleton(Daleel.Web.Translation.TranslationOptions.FromEnvironment());
builder.Services.AddSingleton<Daleel.Web.Translation.ITranslator, Daleel.Web.Translation.DeepLClient>();
builder.Services.AddTransient<ITranslationRepository, TranslationRepository>();
builder.Services.AddTransient<Daleel.Web.Translation.ITranslationService, Daleel.Web.Translation.TranslationService>();

// Cloudflare R2 object storage. Each concern routes to its own bucket: error logs (daleel-logs), product
// /brand/store images (daleel-images), raw + final product specs (daleel-specs) and scraped site/brand
// data (daleel-data). Registered only when R2 is fully configured (shared credentials + endpoint); the
// no-op fallback keeps the original image URLs so the rest of the pipeline is unaffected.
var r2Options = Daleel.Web.Storage.R2Options.FromConfiguration(builder.Configuration);
if (r2Options is not null)
{
    // Image hosting needs a genuinely public object host — the images bucket's public dev URL or a custom
    // domain — supplied via R2_PUBLIC_URL_IMAGES (or the legacy R2_PUBLIC_URL). We deliberately do NOT fall
    // back to the S3 service URL ("{serviceUrl}/{bucket}"): that endpoint requires SigV4 auth and returns 403
    // for the plain GET an <img> tag makes, so every "hosted" image would silently break. When the images
    // public host is unset the service still runs (JSON specs, the admin data browser) but StoreImageAsync
    // hot-links the original URL instead.
    if (string.IsNullOrEmpty(r2Options.Images.PublicUrl))
    {
        Console.WriteLine(
            "[startup] R2 is configured but the images public host (R2_PUBLIC_URL_IMAGES / R2_PUBLIC_URL) is " +
            "unset — product images will be hot-linked from their source rather than served from R2. Set it to " +
            "the images bucket's public URL to host them.");
    }

    builder.Services.AddSingleton<Daleel.Web.Storage.IR2StorageService>(sp =>
        new Daleel.Web.Storage.R2StorageService(
            r2Options,
            // Dedicated SSRF-guarded client (connect-time IP pinning, redirects disabled): this fetch
            // pulls attacker-influenced image URLs onto our own host, so it gets the hardened client.
            Daleel.Search.Http.SsrfGuard.CreateGuardedClient(),
            sp.GetRequiredService<ILogger<Daleel.Web.Storage.R2StorageService>>()));

    // Mirrors the Serilog file sink's day file to R2 in full — replaces the AmazonS3 sink, whose
    // per-batch uploads under one fixed day key clobbered each other, leaving R2 with only the last
    // few seconds of logs (see LogFileMirror). Registered only alongside the real storage service.
    builder.Services.AddHostedService<Daleel.Web.Logging.R2LogMirrorService>();
}
else
{
    builder.Services.AddSingleton<Daleel.Web.Storage.IR2StorageService, Daleel.Web.Storage.NullR2StorageService>();
}

// Cloudflare execution layer (docs/architecture/cloudflare-workers-pipeline.md, Phase 0/1): the client
// the pipeline submits async scrape jobs to, the Queues pull consumer, and the drain service that
// persists finished results INDEPENDENT of any workflow's lifetime — a crawl finishing after a search's
// deadline still lands. Registered only when the worker endpoint is configured (same optional-capability
// pattern as R2); whether the pipeline actually routes work there is the admin-editable
// `cloudflare.execution.enabled` flag, so rollback is a settings toggle.
// ── Token authority (dynamic app↔worker auth) ───────────────────────────────
// The VPS mints, stores (encrypted, Postgres) and rotates the bearers that authenticate this app to
// its Cloudflare workers, pushing them to the workers via the Cloudflare API — GitHub secrets are no
// longer the bearer store (env values remain only as bootstrap fallback). Vendor API keys are also
// vault-manageable from /admin/credentials; resolution is vault-snapshot-first, environment second.
builder.Services.AddSingleton<Daleel.Web.Data.ICredentialVault, Daleel.Web.Data.CredentialVault>();
builder.Services.AddSingleton<Daleel.Web.Cloudflare.ICloudflareSecretsClient>(sp =>
    new Daleel.Web.Cloudflare.CloudflareSecretsClient(
        Daleel.Search.Http.SharedHttpHandler.CreateClient(),
        sp.GetRequiredService<IConfiguration>(),
        sp.GetRequiredService<ILogger<Daleel.Web.Cloudflare.CloudflareSecretsClient>>()));
builder.Services.AddSingleton<Daleel.Web.Cloudflare.CredentialRotationService>();
builder.Services.AddSingleton<Daleel.Web.Cloudflare.ICredentialRotationService>(sp =>
    sp.GetRequiredService<Daleel.Web.Cloudflare.CredentialRotationService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<Daleel.Web.Cloudflare.CredentialRotationService>());

// Vault-first bearer lookup for a worker script of THIS environment ("worker:<script>").
var workerSuffix = Daleel.Web.Cloudflare.WorkerNames.Suffix(builder.Configuration);
Func<IServiceProvider, string, string?> vaultBearer = (sp, baseName) =>
    sp.GetRequiredService<Daleel.Web.Data.ICredentialVault>().TryGetCached($"worker:{baseName}{workerSuffix}");

var cfWorkerOptions = Daleel.Web.Cloudflare.CloudflareWorkerOptions.FromConfiguration(builder.Configuration);
if (cfWorkerOptions is not null && r2Options is null)
{
    // Without R2 the VPS could never read an edge result back — every submitted crawl would be
    // silently stranded. Refuse the half-configuration loudly and keep the inline path authoritative.
    Console.WriteLine(
        "[startup] CF_SCRAPE_WORKER_URL is set but R2 is not configured — edge results could never be " +
        "read back, so the Cloudflare execution layer is DISABLED. Set R2_ACCESS_KEY/R2_SECRET_KEY.");
    cfWorkerOptions = null;
}
if (cfWorkerOptions is not null)
{
    builder.Services.AddSingleton(cfWorkerOptions);
    builder.Services.AddSingleton<Daleel.Web.Cloudflare.ICloudflareWorkerClient>(sp =>
        new Daleel.Web.Cloudflare.CloudflareWorkerClient(
            Daleel.Search.Http.SharedHttpHandler.CreateClient(),
            cfWorkerOptions,
            sp.GetRequiredService<Daleel.Web.Storage.IR2StorageService>(),
            sp.GetRequiredService<ILogger<Daleel.Web.Cloudflare.CloudflareWorkerClient>>(),
            bearer: () => vaultBearer(sp, "daleel-scrape-worker")));
    builder.Services.AddSingleton<Daleel.Web.Cloudflare.IQueuePullClient>(sp =>
        new Daleel.Web.Cloudflare.QueuePullClient(
            Daleel.Search.Http.SharedHttpHandler.CreateClient(),
            cfWorkerOptions,
            sp.GetRequiredService<ILogger<Daleel.Web.Cloudflare.QueuePullClient>>()));
    builder.Services.AddHostedService<Daleel.Web.Cloudflare.CloudflarePollDrainService>();
}

// Transactional email (Resend). When RESEND_API_KEY is set, search-completion emails are sent via the
// Resend HTTP API; otherwise a no-op service drops them — the same optional-capability pattern as the
// event store and R2. EMAIL_FROM sets the sender (default noreply@daleel.hamzalabs.dev) and APP_BASE_URL
// is the public site URL the "View Full Report" button links back to.
var resendApiKey = builder.Configuration["RESEND_API_KEY"]?.Trim();
var emailFrom = builder.Configuration["EMAIL_FROM"]?.Trim();
if (string.IsNullOrEmpty(emailFrom))
{
    emailFrom = "noreply@daleel.hamzalabs.dev";
}
var appBaseUrl = builder.Configuration["APP_BASE_URL"]?.Trim();
if (string.IsNullOrEmpty(appBaseUrl))
{
    appBaseUrl = "https://daleel.hamzalabs.dev";
}
builder.Services.AddSingleton(new Daleel.Web.Email.EmailNotificationOptions(appBaseUrl));
if (!string.IsNullOrEmpty(resendApiKey))
{
    builder.Services.AddSingleton<Daleel.Web.Email.IEmailService>(sp =>
        new Daleel.Web.Email.ResendEmailService(
            resendApiKey, emailFrom!,
            Daleel.Search.Http.SharedHttpHandler.CreateClient(),
            sp.GetRequiredService<ILogger<Daleel.Web.Email.ResendEmailService>>()));
}
else
{
    builder.Services.AddSingleton<Daleel.Web.Email.IEmailService, Daleel.Web.Email.NullEmailService>();
}
builder.Services.AddScoped<Daleel.Web.Email.IUserEmailPreferences, Daleel.Web.Email.UserEmailPreferences>();
builder.Services.AddScoped<Daleel.Web.Email.SearchResultEmailTemplate>();
builder.Services.AddScoped<Daleel.Web.Email.ISearchEmailNotifier, Daleel.Web.Email.SearchEmailNotifier>();

builder.Services.AddScoped<Daleel.Web.Pipeline.IItemEnrichmentService, Daleel.Web.Pipeline.ItemEnrichmentService>();
builder.Services.AddSingleton(new Daleel.Web.Profiles.ProfileOptions());
builder.Services.AddSingleton<Daleel.Web.Profiles.IProfileResearcher, Daleel.Web.Profiles.ContextDevProfileResearcher>();
builder.Services.AddScoped<Daleel.Web.Profiles.IBrandProfileService, Daleel.Web.Profiles.BrandProfileService>();
builder.Services.AddScoped<Daleel.Web.Profiles.IStoreProfileService, Daleel.Web.Profiles.StoreProfileService>();
builder.Services.AddScoped<Daleel.Web.Profiles.IBrandCatalogService, Daleel.Web.Profiles.BrandCatalogService>();
builder.Services.AddHostedService<Daleel.Web.Profiles.ProfileRefreshService>();
// Entity dedup: recurring identity-key backfill + exact-duplicate merge (off/dry-run by default).
builder.Services.AddSingleton<Daleel.Web.Persistence.EntityDedupService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Daleel.Web.Persistence.EntityDedupService>());

// Smart product identification: matches a (often vaguely-named) Jordanian store listing to the canonical
// brand model — text match → cross-region catalogue discovery → cached vision match — then runs the spec
// pipeline (raw → merge/clean → canonical sheet). The vision matcher uses the server's OpenRouter key
// (identification runs server-side during background enrichment); without a key the no-op matcher makes
// every comparison a non-match, so the pipeline degrades to text-only identification.
builder.Services.AddScoped<Daleel.Web.Data.IVisionMatchCacheRepository, Daleel.Web.Data.VisionMatchCacheRepository>();
builder.Services.AddSingleton<Daleel.Web.Identification.ISpecMerger, Daleel.Web.Identification.SpecMerger>();
builder.Services.AddScoped<Daleel.Web.Identification.IBrandCatalogSearcher, Daleel.Web.Identification.BrandCatalogSearcher>();
builder.Services.AddScoped<Daleel.Web.Identification.IProductIdentifier, Daleel.Web.Identification.SmartProductIdentifier>();
builder.Services.AddSingleton<Daleel.Web.Identification.IVisionMatcher>(sp =>
{
    var factory = sp.GetRequiredService<IAgentFactory>();
    var key = factory.Resolve("OPENROUTER_API_KEY");
    if (string.IsNullOrWhiteSpace(key))
    {
        return new Daleel.Web.Identification.NullVisionMatcher();
    }

    var model = factory.Resolve("OPENROUTER_VISION_MODEL");
    return new Daleel.Web.Identification.VisionMatcher(
        key, model, sp.GetRequiredService<ILogger<Daleel.Web.Identification.VisionMatcher>>());
});

// Async conversation backend: SignalR + a Postgres-polling background worker run searches off the request.
// There is no in-memory job queue — "queued" SearchJob rows in Postgres ARE the queue (see SearchJobService).
builder.Services.AddSignalR();
builder.Services.AddSingleton<Daleel.Web.Conversation.SignalRConversationBroadcaster>();
builder.Services.AddSingleton<Daleel.Web.Conversation.IConversationBroadcaster>(sp => sp.GetRequiredService<Daleel.Web.Conversation.SignalRConversationBroadcaster>());
builder.Services.AddSingleton<Daleel.Web.Conversation.IConversationNotifier>(sp => sp.GetRequiredService<Daleel.Web.Conversation.SignalRConversationBroadcaster>());
// The search pipeline runs as an in-process Elsa 3 workflow of CodeActivity steps (plan → cache →
// gather → extract → dispatch brand/store/item sub-workflows → aggregate → moderate → cache → return).
// AddActivitiesFrom scans the whole assembly, so the sub-workflow activities under Pipeline/SubWorkflows
// are discovered automatically.
//
// Workflow-instance persistence is OPTIONAL and Postgres-only, mirroring the event store: when
// POSTGRES_CONNECTION_STRING/DATABASE_URL is set, the workflow-management feature is registered on Elsa's
// EF Core Postgres provider (the same connection the event store uses — see PostgresConnection.Resolve),
// which gives IWorkflowInstanceStore + IWorkflowInstanceManager. Its schema is migrated explicitly in
// EnsureDatabase at startup (Elsa's auto-migration only runs under UseWorkflowRuntime, which we don't use).
// When Postgres is NOT configured, persistence is simply unavailable — no in-memory or SQLite
// fallback — and the management feature is not registered; the search workflow still runs in-process via
// IWorkflowRunner, the runner skips its (best-effort) instance save, and the admin workflows page shows a
// "not configured" notice. If you want instance tracking, configure Postgres.
//
// Registering the instance store is safe now (it used to be blocked by a startup assertion): the pipeline no
// longer shares a live AgentService + progress delegate through SearchPipelineState — those moved to the
// separate scoped SearchPipelineServices/SubWorkflowServices and never touch the persisted WorkflowState.
// NOTE: this enables persisting *completed-run summaries* for the admin page, NOT mid-run suspend/resume. The
// working run state lives in the DI scope (not WorkflowState), so the workflow must run to completion in one
// pass — do not add Delay/bookmarks/suspend activities (a resume would see blank state).
var elsaInstanceConn = Daleel.Web.Events.PostgresConnection.Resolve(builder.Configuration);
builder.Services.AddElsa(elsa =>
{
    elsa.AddActivitiesFrom<Daleel.Web.Pipeline.SearchWorkflow>();
    if (elsaInstanceConn is not null)
    {
        elsa.UseWorkflowManagement(management =>
            management.UseEntityFrameworkCore(ef => ef.UsePostgreSql(elsaInstanceConn)));
    }
});

// The serializable run state + the live (non-serializable) services half are both scoped, so each run's
// DI scope gets its own pair: the runner seeds them, the Elsa activities resolve them from their context.
builder.Services.AddScoped<Daleel.Web.Pipeline.SearchPipelineState>();
builder.Services.AddScoped<Daleel.Web.Pipeline.SearchPipelineServices>();
// Per-entity sub-workflow states — scoped so each dispatched child (in its own DI scope) gets a fresh one.
builder.Services.AddScoped<Daleel.Web.Pipeline.SubWorkflows.BrandResearchState>();
builder.Services.AddScoped<Daleel.Web.Pipeline.SubWorkflows.StoreResearchState>();
builder.Services.AddScoped<Daleel.Web.Pipeline.SubWorkflows.ItemDeepDiveState>();
builder.Services.AddScoped<Daleel.Web.Pipeline.SubWorkflows.StoreCrawlState>();
builder.Services.AddScoped<Daleel.Web.Pipeline.SubWorkflows.BrandCrawlState>();
builder.Services.AddScoped<Daleel.Web.Pipeline.SubWorkflows.ProductDetailState>();
builder.Services.AddScoped<Daleel.Web.Pipeline.SubWorkflows.SubWorkflowServices>();
builder.Services.AddScoped<Daleel.Web.Conversation.ISearchRunner, Daleel.Web.Conversation.WorkflowSearchRunner>();
// The Workers-AI fleet hosts (classify / extract / filter — doc §3.2–3.4). Signals only: policy
// stays on the VPS, and routing into the moderation/extraction hot paths waits for the doc's
// mandated A/B validation. Registered only when at least one endpoint is configured.
if (Daleel.Web.Cloudflare.CloudflareFleetOptions.FromConfiguration(builder.Configuration) is { } fleetOptions)
{
    builder.Services.AddSingleton<Daleel.Web.Cloudflare.ICloudflareFleetClient>(sp =>
        new Daleel.Web.Cloudflare.CloudflareFleetClient(
            fleetOptions,
            Daleel.Search.Http.SharedHttpHandler.CreateClient,
            sp.GetRequiredService<ILogger<Daleel.Web.Cloudflare.CloudflareFleetClient>>(),
            bearer: capability => vaultBearer(sp, $"daleel-{capability}-worker")));
}

// THE provider gateway: every provider call made outside an AgentService flows through this — metered
// (ambient per-job observer), cost-estimated, cap-enforced by construction. Never construct a provider
// directly at a call site. Edge submits + fleet signals route through it too, so all spend meters identically.
builder.Services.AddSingleton<Daleel.Web.Services.IProviderApi>(sp =>
    new Daleel.Web.Services.ProviderApi(
        sp.GetRequiredService<Daleel.Web.Services.IAgentFactory>(),
        sp.GetService<Daleel.Web.Cloudflare.ICloudflareWorkerClient>(),
        sp.GetService<Daleel.Web.Cloudflare.ICloudflareFleetClient>(),
        sp.GetService<Daleel.Web.Cloudflare.CloudflareWorkerOptions>(),
        sp.GetService<Daleel.Web.Storage.IR2StorageService>()));
// Smart cache: scores a cache hit's completeness so CheckCache can serve, serve-and-refill, or reject it.
// Stateless + side-effect-free, so a singleton is fine (resolved by the Elsa activity from its context).
builder.Services.AddSingleton<Daleel.Web.Pipeline.ICacheQualityValidator, Daleel.Web.Pipeline.CacheQualityValidator>();
// Transient: injected into the interactive Home component (and resolved per-use by the background worker),
// so it must not share a circuit-scoped DbContext with concurrently-rendering components.
builder.Services.AddTransient<Daleel.Web.Conversation.IConversationStore, Daleel.Web.Conversation.ConversationStore>();
builder.Services.AddTransient<Daleel.Web.Conversation.IConversationService, Daleel.Web.Conversation.ConversationService>();
builder.Services.AddHostedService<Daleel.Web.Conversation.SearchJobService>();
// Periodic safety sweep (every 5 min): force-cancels jobs whose durable CancelRequested flag is set but
// are still "running", and fails jobs wedged past the 12-minute hung threshold. Complements the boot-time
// OrphanedJobReconciler so a job can never spin forever between restarts. See JobReconciliationService.
builder.Services.AddHostedService<Daleel.Web.Conversation.JobReconciliationService>();

// Enrichment WORK QUEUE (Pipeline/Enrichment): post-result deep-dives run as durable per-unit work
// items — one API dive per item, own retries/budget, saved the moment each lands. The EnrichmentWorkItems
// table IS the queue (FOR UPDATE SKIP LOCKED claims, lease-expiry crash recovery); there is deliberately
// no job- or phase-level enrichment timeout anywhere. Handlers are stateless singletons that resolve
// scoped services per execution.
builder.Services.AddSingleton<Daleel.Web.Pipeline.Enrichment.IEnrichmentWorkQueue,
    Daleel.Web.Pipeline.Enrichment.EnrichmentWorkQueue>();
builder.Services.AddSingleton<Daleel.Web.Pipeline.Enrichment.IEnrichedResultStore,
    Daleel.Web.Pipeline.Enrichment.EnrichedResultStore>();
// Per-search/product/brand work contexts: findings ledger + LLM synthesis (opens its own scope per call).
builder.Services.AddSingleton<Daleel.Web.Data.IWorkContextStore, Daleel.Web.Data.WorkContextStore>();
// The LLM-as-actor engine: a bounded reason→act→observe loop reused by converted provider-calling steps.
builder.Services.AddSingleton<Daleel.Web.Pipeline.Enrichment.Actor.IActorLoop,
    Daleel.Web.Pipeline.Enrichment.Actor.ActorLoop>();
builder.Services.AddSingleton<Daleel.Web.Pipeline.Enrichment.Actor.ItemDiveActor>();
builder.Services.AddSingleton<Daleel.Web.Pipeline.Enrichment.Actor.VerifyPageActor>();
builder.Services.AddSingleton<Daleel.Web.Pipeline.Enrichment.Actor.BrandSiteActor>();
builder.Services.AddSingleton<Daleel.Web.Pipeline.Enrichment.Actor.CatalogActor>();
builder.Services.AddSingleton<Daleel.Web.Pipeline.Enrichment.Actor.StoreSiteActor>();
builder.Services.AddSingleton<Daleel.Web.Pipeline.Enrichment.IEnrichmentUnitHandler,
    Daleel.Web.Pipeline.Enrichment.PlanEnrichmentHandler>();
builder.Services.AddSingleton<Daleel.Web.Pipeline.Enrichment.IEnrichmentUnitHandler,
    Daleel.Web.Pipeline.Enrichment.ItemDiveHandler>();
builder.Services.AddSingleton<Daleel.Web.Pipeline.Enrichment.IEnrichmentUnitHandler,
    Daleel.Web.Pipeline.Enrichment.VisionUnitHandler>();
builder.Services.AddSingleton<Daleel.Web.Pipeline.Enrichment.IEnrichmentUnitHandler,
    Daleel.Web.Pipeline.Enrichment.CatalogAttachHandler>();
builder.Services.AddSingleton<Daleel.Web.Pipeline.Enrichment.IEnrichmentUnitHandler,
    Daleel.Web.Pipeline.Enrichment.BrandResearchHandler>();
builder.Services.AddSingleton<Daleel.Web.Pipeline.Enrichment.IEnrichmentUnitHandler,
    Daleel.Web.Pipeline.Enrichment.ImageLookupHandler>();
builder.Services.AddSingleton<Daleel.Web.Pipeline.Enrichment.IEnrichmentUnitHandler,
    Daleel.Web.Pipeline.Enrichment.ConditionsHandler>();
builder.Services.AddSingleton<Daleel.Web.Pipeline.Enrichment.IEnrichmentUnitHandler,
    Daleel.Web.Pipeline.Enrichment.CacheGapRefillHandler>();
builder.Services.AddSingleton<Daleel.Web.Pipeline.Enrichment.IEnrichmentUnitHandler,
    Daleel.Web.Pipeline.Enrichment.OfferVerificationHandler>();
builder.Services.AddSingleton<Daleel.Web.Pipeline.Enrichment.IEnrichmentUnitHandler,
    Daleel.Web.Pipeline.Enrichment.VerifyPageHandler>();
builder.Services.AddSingleton<Daleel.Web.Pipeline.Enrichment.IReachabilityProbe>(_ =>
    new Daleel.Web.Pipeline.Enrichment.ReachabilityProbe(Daleel.Search.Http.SharedHttpHandler.CreateClient()));
builder.Services.AddSingleton<Daleel.Web.Pipeline.Enrichment.IEnrichmentUnitHandler,
    Daleel.Web.Pipeline.Enrichment.ReachabilityHandler>();
builder.Services.AddSingleton<Daleel.Web.Pipeline.Enrichment.IEnrichmentUnitHandler,
    Daleel.Web.Pipeline.Enrichment.SynthesisHandler>();
builder.Services.AddSingleton<Daleel.Web.Pipeline.Enrichment.IEnrichmentUnitHandler,
    Daleel.Web.Pipeline.Enrichment.ImageCheckHandler>();
// Re-reads the detail page of an item the product-shot screen left imageless (ImageCheck enqueues it).
builder.Services.AddSingleton<Daleel.Web.Pipeline.Enrichment.IEnrichmentUnitHandler,
    Daleel.Web.Pipeline.Enrichment.CleanShotHandler>();
// Inventory monitor: Shopify catalogue client + the sync/page/finalize units + the scheduler.
builder.Services.AddSingleton<Daleel.Web.Pipeline.Inventory.IStoreCatalogClient>(sp =>
    new Daleel.Web.Pipeline.Inventory.CompositeCatalogClient(
        new Daleel.Web.Pipeline.Inventory.ShopifyCatalogClient(
            providers: sp.GetRequiredService<Daleel.Web.Services.IProviderApi>()),
        new Daleel.Web.Pipeline.Inventory.WooCommerceCatalogClient(
            providers: sp.GetRequiredService<Daleel.Web.Services.IProviderApi>())));
builder.Services.AddSingleton<Daleel.Web.Pipeline.Enrichment.IEnrichmentUnitHandler,
    Daleel.Web.Pipeline.Inventory.InventorySyncHandler>();
builder.Services.AddSingleton<Daleel.Web.Pipeline.Enrichment.IEnrichmentUnitHandler,
    Daleel.Web.Pipeline.Inventory.InventoryPageHandler>();
// HTML mode: listing-page discovery (sitemap first, one LLM homepage assess as fallback) + the
// per-listing-page walk unit for stores with no machine-readable catalogue.
builder.Services.AddSingleton<Daleel.Web.Pipeline.Inventory.IHtmlCatalogDiscovery>(sp =>
    new Daleel.Web.Pipeline.Inventory.HtmlCatalogDiscovery(
        sp.GetRequiredService<Daleel.Web.Services.IProviderApi>()));
builder.Services.AddSingleton<Daleel.Web.Pipeline.Enrichment.IEnrichmentUnitHandler,
    Daleel.Web.Pipeline.Inventory.InventoryHtmlPageHandler>();
builder.Services.AddSingleton<Daleel.Web.Pipeline.Enrichment.IEnrichmentUnitHandler,
    Daleel.Web.Pipeline.Inventory.InventoryFinalizeHandler>();
builder.Services.AddHostedService<Daleel.Web.Pipeline.Inventory.StoreInventoryMonitorService>();
builder.Services.AddHostedService<Daleel.Web.Pipeline.Enrichment.EnrichmentQueueService>();
// Read-side of /admin/queues (scoped: one DbContext per dashboard refresh tick).
builder.Services.AddScoped<Daleel.Web.Pipeline.Enrichment.IQueueDashboardService,
    Daleel.Web.Pipeline.Enrichment.QueueDashboardService>();

// Search cache: Postgres-backed store (singleton — opens its own DbContext scope per call, since the
// agent runs providers in parallel) plus a weekly background sweep of expired entries.
builder.Services.AddSingleton<Daleel.Core.Caching.ICacheStore, Daleel.Web.Data.PostgresCacheStore>();
builder.Services.AddHostedService<Daleel.Web.Services.CacheCleanupService>();

// Saved-catalogue hygiene: every 6 hours, sweep the brand/store/product tables and drop rows that are
// too thin (no price/source/name) or were misclassified during a search (an article saved as a product).
// Idempotent — a clean table is a no-op — so it's safe to run repeatedly.
builder.Services.AddHostedService<Daleel.Web.Services.DataCleanupService>();

// Admin-triggered, irreversible bulk wipes (search cache, catalogue, workflow history) behind the
// /admin/cleardata page. Transient: it gets its own DaleelDbContext, never sharing the circuit's.
builder.Services.AddTransient<Daleel.Web.Services.IClearDataService, Daleel.Web.Services.ClearDataService>();

// IP rate limiting (in-memory fixed-window — no Redis at this scale).
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<IIpRateLimiter, IpRateLimiter>();
builder.Services.AddSingleton<IAgentFactory, AgentFactory>();
// Product/brand/store detail pages read from the saved database (specs, R2 images, scraped prices)
// rather than re-scraping live; this assembles the product view from those tables.
builder.Services.AddTransient<IProductDetailDbService, ProductDetailDbService>();
builder.Services.AddSingleton<MonitorService>();

// /status page: HTTP probes against each provider's host + last-search lookup.
builder.Services.AddHttpClient();
builder.Services.AddTransient<IStatusService, StatusService>();
// Live OpenRouter model catalogue for the admin per-call-site model picker (fetched, briefly cached).
builder.Services.AddSingleton<IOpenRouterCatalog, OpenRouterCatalog>();
// QA-only raw provider diagnostics (gated by DIAGNOSTICS_ENABLED — off in production).
builder.Services.AddScoped<IProviderDiagnostics, ProviderDiagnostics>();

// Liveness probe consumed by the Docker HEALTHCHECK, deploy.sh, and Caddy upstream checks.
builder.Services.AddHealthChecks();

// Running behind Caddy (TLS terminator), so honour X-Forwarded-* to keep redirects/HSTS correct.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // The proxy is a known peer on the container network; clearing these accepts the hop from Caddy.
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

var app = builder.Build();

// Create the app database if needed and apply any pending migrations on boot.
EnsureDatabase(app);

app.UseForwardedHeaders();

// 1. Rate limiting first — reject abusive IPs before any auth/quota/render work.
app.UseMiddleware<Daleel.Web.RateLimiting.IpRateLimitMiddleware>();

// Mapped before HTTPS redirection so the internal http://localhost:8080/health probe gets 200, not 307.
app.MapHealthChecks("/health");

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseStaticFiles();

// Resolve the request culture (cookie → Accept-Language) before components render.
app.UseRequestLocalization(app.Services.GetRequiredService<
    Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Builder.RequestLocalizationOptions>>().Value);

app.UseAuthentication();
app.UseAuthorization();

app.UseAntiforgery();

// Sets the culture cookie and redirects back — the language switcher posts here.
app.MapGet("/set-language", (string culture, string? redirectUri, HttpContext ctx) =>
{
    var safe = culture is "ar" or "en" ? culture : "en";
    ctx.Response.Cookies.Append(
        Microsoft.AspNetCore.Localization.CookieRequestCultureProvider.DefaultCookieName,
        Microsoft.AspNetCore.Localization.CookieRequestCultureProvider.MakeCookieValue(
            new Microsoft.AspNetCore.Localization.RequestCulture(safe)),
        new CookieOptions { Expires = DateTimeOffset.UtcNow.AddYears(1), IsEssential = true, Path = "/" });
    var target = string.IsNullOrWhiteSpace(redirectUri) || !redirectUri.StartsWith('/') ? "/" : redirectUri;
    return Results.LocalRedirect(target);
});

app.MapAuthEndpoints();
app.MapConversationEndpoints();
app.MapHub<Daleel.Web.Conversation.ConversationHub>("/hubs/conversation");

// Single renderer: the server circuit. No WebAssembly render mode and no component-less Client
// assembly — see the AddRazorComponents comment above. One renderer = no ambiguity over which renderer
// owns a JS interop call.
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

static void EnsureDatabase(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    // Migrate() creates the `daleel` database on the Postgres server if it does not yet exist, then
    // applies any pending migrations. The bundled compose `postgres` service only seeds `daleel_events`,
    // so the very first boot provisions the app database here.
    var db = scope.ServiceProvider.GetRequiredService<DaleelDbContext>();
    db.Database.Migrate();

    // Crash recovery: fail any jobs left "running" by a previous container's death (deploy/crash/OOM) before
    // the worker starts. This runs here in EnsureDatabase — synchronously, before app.Run() starts the hosted
    // SearchJobService — so no new job is picked up until the orphaned zombies are reconciled. See
    // OrphanedJobReconciler.
    Daleel.Web.Conversation.OrphanedJobReconciler
        .ReconcileAsync(db, app.Logger).GetAwaiter().GetResult();

    // Seed admin-editable system settings (idempotent).
    scope.ServiceProvider.GetRequiredService<ISystemConfigService>().SeedDefaultsAsync().GetAwaiter().GetResult();
    // Seed the halal image-moderation rule list from the built-in defaults on first run (idempotent).
    scope.ServiceProvider.GetRequiredService<IImageModerationRuleRepository>().SeedDefaultsIfEmptyAsync().GetAwaiter().GetResult();

    // Bring the event store's `daleel_events` database up to schema. The app DB Migrate() above already
    // proved the Postgres server is reachable, but the event store is a separate database, so keep this
    // best-effort: a transient failure here must degrade to dropping events, never stop the app.
    var eventDbFactory = scope.ServiceProvider
        .GetService<IDbContextFactory<Daleel.Web.Events.EventStoreDbContext>>();
    if (eventDbFactory is not null)
    {
        try
        {
            using var eventDb = eventDbFactory.CreateDbContext();
            eventDb.Database.Migrate();
        }
        catch (Exception ex)
        {
            app.Logger.LogWarning(ex, "Event store migration skipped — Postgres unavailable at startup");
        }
    }

    // Bring Elsa's workflow-instance store up to schema. Elsa's EF Core management feature is supposed to
    // apply its own migrations at startup, but that auto-migration only fires under UseWorkflowRuntime — we
    // deliberately run in-process via IWorkflowRunner + UseWorkflowManagement only, so it never ran and the
    // first instance save hit `42P01: relation "Elsa.WorkflowInstances" does not exist`. Apply the
    // Elsa-shipped migrations explicitly here, exactly like the app DB and event store above. The factory is
    // only registered when Postgres is configured (UseWorkflowManagement is conditional on elsaInstanceConn),
    // so this no-ops when persistence is disabled. Best-effort: a transient failure must not stop the app —
    // the runner's instance save is already best-effort and the workflow still runs in-process.
    var elsaDbFactory = scope.ServiceProvider
        .GetService<IDbContextFactory<Elsa.EntityFrameworkCore.Modules.Management.ManagementElsaDbContext>>();
    if (elsaDbFactory is not null)
    {
        try
        {
            using var elsaDb = elsaDbFactory.CreateDbContext();
            elsaDb.Database.Migrate();
        }
        catch (Exception ex)
        {
            app.Logger.LogWarning(ex, "Elsa workflow-instance store migration skipped — Postgres unavailable at startup");
        }
    }
}
