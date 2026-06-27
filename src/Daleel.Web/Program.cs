using Daleel.Web.Auth;
using Daleel.Web.Components;
using Daleel.Web.Conversation;
using Daleel.Web.Data;
using Daleel.Web.Logging;
using Daleel.Web.RateLimiting;
using Daleel.Web.Services;
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

// ── Logging (Serilog) ─────────────────────────────────────────────────────────
// Replace the default console logger with Serilog: Information+ to the console for debugging, and
// Warning and above as JSON Lines to Cloudflare R2 (when R2_* env vars are set) or to local files
// under /app/data/logs otherwise. See Daleel.Web.Logging.SerilogConfiguration.
builder.AddDaleelLogging();

// Razor components with both interactive runtimes (Server for the secret-bearing agent pages,
// WebAssembly available for the Auto runtime).
//
// DetailedErrors surfaces the full server-side stack trace of any unhandled circuit exception to the
// browser (instead of the opaque "Unhandled exception on the current circuit"). It's config-driven so
// it can be toggled per environment: it defaults OFF in base appsettings.json (don't leak stack traces
// to clients) and is enabled only in appsettings.Development.json for local debugging. See SEC-2.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents(options =>
        options.DetailedErrors = builder.Configuration.GetValue("DetailedErrors", false))
    .AddInteractiveWebAssemblyComponents();

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

// ── Persistence ────────────────────────────────────────────────────────────
// SQLite locally (data/daleel.db); swap UseSqlite → UseNpgsql for Postgres without model changes.
var connection = builder.Configuration.GetConnectionString("DefaultConnection")
                 ?? "Data Source=data/daleel.db";
builder.Services.AddDbContext<DaleelDbContext>(o => o.UseSqlite(connection));

// ── Pipeline event store (PostgreSQL, optional) ───────────────────────────────
// A separate append-only store on Postgres records every pipeline action (provider call, scrape,
// LLM, cache, profile, places, extract) for the admin usage/cost dashboard. It is OPTIONAL: with no
// POSTGRES_CONNECTION_STRING / DATABASE_URL set, the app runs SQLite-only and the store is a no-op.
var eventStoreConn = Daleel.Web.Events.PostgresConnection.Resolve(builder.Configuration);
if (eventStoreConn is not null)
{
    builder.Services.AddDbContextFactory<Daleel.Web.Events.EventStoreDbContext>(
        o => o.UseNpgsql(eventStoreConn));
    builder.Services.AddSingleton<Daleel.Web.Events.IEventStore, Daleel.Web.Events.PostgresEventStore>();
}
else
{
    builder.Services.AddSingleton<Daleel.Web.Events.IEventStore, Daleel.Web.Events.NullEventStore>();
}

// ── Data Protection (auth-cookie encryption keys) ─────────────────────────────
// ASP.NET encrypts the Identity auth cookie with Data Protection keys. By default those keys live in
// $HOME/.aspnet/DataProtection-Keys, but the container runs as a --no-create-home user (see Dockerfile),
// so there is no $HOME and the key ring falls back to IN-MEMORY storage. New keys are then minted on every
// container start, which makes every previously-issued cookie undecryptable — i.e. every user is silently
// signed out on each redeploy/restart. Persisting the keys to the data/ directory fixes this: that path
// maps to the `daleel_data` named volume (owned by the app user) and survives redeploys, just like the
// SQLite DB next to it. Path is configurable; defaults alongside the DB so container + local both work.
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
builder.Services.AddScoped<ICurrentUser, CurrentUser>(); // authenticated id, from the circuit
builder.Services.AddScoped<ISearchHistoryRepository, SearchHistoryRepository>();
builder.Services.AddScoped<ISavedResultRepository, SavedResultRepository>();
builder.Services.AddScoped<IQuotaService, QuotaService>();
builder.Services.AddScoped<IAnalyticsService, AnalyticsService>();
builder.Services.AddScoped<ISystemConfigService, SystemConfigService>();
builder.Services.AddScoped<IApiCallLogRepository, ApiCallLogRepository>();
builder.Services.AddScoped<IFilteredContentLogRepository, FilteredContentLogRepository>();
builder.Services.AddHttpContextAccessor();

// Persistent brand/store profiles: researched once via Context.dev + the LLM, saved to SQLite, and
// refreshed only when stale (>30 days). Search results JOIN against these instead of re-fetching.
builder.Services.AddScoped<IBrandRepository, BrandRepository>();
builder.Services.AddScoped<IStoreRepository, StoreRepository>();
builder.Services.AddScoped<IProductProfileRepository, ProductProfileRepository>();
builder.Services.AddScoped<IBrandModelRepository, BrandModelRepository>();
builder.Services.AddScoped<IScrapedPriceRepository, ScrapedPriceRepository>();

// Cloudflare R2 image storage. Scraped product/brand images are copied into R2 and the hosted URL is
// stored instead of hot-linking the external source. Registered only when R2 is fully configured; the
// no-op fallback keeps the original URLs so the rest of the pipeline is unaffected.
var r2Options = Daleel.Web.Logging.R2LoggingOptions.FromConfiguration(builder.Configuration);
if (r2Options is not null)
{
    // The S3 service URL isn't a public object host, so prefer an explicit R2_PUBLIC_URL (a bucket
    // public dev URL or custom domain). Falling back to "{serviceUrl}/{bucket}" keeps the stored URL
    // well-formed even if the bucket isn't publicly served yet.
    var publicBase = builder.Configuration["R2_PUBLIC_URL"]?.Trim();
    if (string.IsNullOrEmpty(publicBase))
    {
        publicBase = $"{r2Options.ServiceUrl.TrimEnd('/')}/{r2Options.BucketName}";
    }

    builder.Services.AddSingleton<Daleel.Web.Storage.IR2StorageService>(sp =>
        new Daleel.Web.Storage.R2StorageService(
            r2Options, publicBase!,
            Daleel.Search.Http.SharedHttpHandler.CreateClient(),
            sp.GetRequiredService<ILogger<Daleel.Web.Storage.R2StorageService>>()));
}
else
{
    builder.Services.AddSingleton<Daleel.Web.Storage.IR2StorageService, Daleel.Web.Storage.NullR2StorageService>();
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

// Async conversation backend: SignalR + a queue + a background worker run searches off the request.
builder.Services.AddSignalR();
builder.Services.AddSingleton<Daleel.Web.Conversation.ISearchJobQueue, Daleel.Web.Conversation.SearchJobQueue>();
builder.Services.AddSingleton<Daleel.Web.Conversation.SignalRConversationBroadcaster>();
builder.Services.AddSingleton<Daleel.Web.Conversation.IConversationBroadcaster>(sp => sp.GetRequiredService<Daleel.Web.Conversation.SignalRConversationBroadcaster>());
builder.Services.AddSingleton<Daleel.Web.Conversation.IConversationNotifier>(sp => sp.GetRequiredService<Daleel.Web.Conversation.SignalRConversationBroadcaster>());
// The search pipeline runs as an in-process Elsa 3 workflow of CodeActivity steps (plan → cache →
// gather → extract → dispatch brand/store/item sub-workflows → aggregate → moderate → cache → return).
// Elsa is registered core-only (no Server/Studio/EF persistence), and the workflow runner replaces the
// old direct-AskAsync runner. AddActivitiesFrom scans the whole assembly, so the sub-workflow activities
// under Pipeline/SubWorkflows are discovered automatically.
builder.Services.AddElsa(elsa => elsa.AddActivitiesFrom<Daleel.Web.Pipeline.SearchWorkflow>());
builder.Services.AddScoped<Daleel.Web.Pipeline.SearchPipelineState>();
// Per-entity sub-workflow states — scoped so each dispatched child (in its own DI scope) gets a fresh one.
builder.Services.AddScoped<Daleel.Web.Pipeline.SubWorkflows.BrandResearchState>();
builder.Services.AddScoped<Daleel.Web.Pipeline.SubWorkflows.StoreResearchState>();
builder.Services.AddScoped<Daleel.Web.Pipeline.SubWorkflows.ItemDeepDiveState>();
builder.Services.AddScoped<Daleel.Web.Conversation.ISearchRunner, Daleel.Web.Conversation.WorkflowSearchRunner>();
builder.Services.AddScoped<Daleel.Web.Conversation.IConversationStore, Daleel.Web.Conversation.ConversationStore>();
builder.Services.AddScoped<Daleel.Web.Conversation.IConversationService, Daleel.Web.Conversation.ConversationService>();
builder.Services.AddHostedService<Daleel.Web.Conversation.SearchJobService>();

// Search cache: SQLite-backed store (singleton — opens its own DbContext scope per call, since the
// agent runs providers in parallel) plus a weekly background sweep of expired entries.
builder.Services.AddSingleton<Daleel.Core.Caching.ICacheStore, Daleel.Web.Data.SqliteCacheStore>();
builder.Services.AddHostedService<Daleel.Web.Services.CacheCleanupService>();

// IP rate limiting (in-memory fixed-window — no Redis at this scale).
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<IIpRateLimiter, IpRateLimiter>();
builder.Services.AddSingleton<IAgentFactory, AgentFactory>();
builder.Services.AddScoped<IModelDetailService, ModelDetailService>();
builder.Services.AddSingleton<MonitorService>();

// /status page: HTTP probes against each provider's host + last-search lookup.
builder.Services.AddHttpClient();
builder.Services.AddScoped<IStatusService, StatusService>();
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

// Ensure the SQLite directory exists, then apply any pending migrations on boot.
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

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(Daleel.Web.Client._Imports).Assembly);

app.Run();

static void EnsureDatabase(WebApplication app)
{
    var connection = app.Configuration.GetConnectionString("DefaultConnection")
                     ?? "Data Source=data/daleel.db";
    var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connection);
    var dir = Path.GetDirectoryName(builder.DataSource);
    if (!string.IsNullOrEmpty(dir))
    {
        Directory.CreateDirectory(dir);
    }

    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<DaleelDbContext>();
    db.Database.Migrate();

    // Seed admin-editable system settings (idempotent).
    scope.ServiceProvider.GetRequiredService<ISystemConfigService>().SeedDefaultsAsync().GetAwaiter().GetResult();

    // Bring the Postgres event store up to schema when it's configured. Best-effort: a Postgres that
    // is unreachable at boot must not stop the app — the event store just degrades to dropping writes.
    var events = scope.ServiceProvider.GetService<Daleel.Web.Events.IEventStore>();
    if (events?.IsEnabled == true)
    {
        try
        {
            var factory = scope.ServiceProvider
                .GetRequiredService<IDbContextFactory<Daleel.Web.Events.EventStoreDbContext>>();
            using var eventDb = factory.CreateDbContext();
            eventDb.Database.Migrate();
        }
        catch (Exception ex)
        {
            app.Logger.LogWarning(ex, "Event store migration skipped — Postgres unavailable at startup");
        }
    }
}
