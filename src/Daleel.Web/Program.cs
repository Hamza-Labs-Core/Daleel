using Daleel.Web.Auth;
using Daleel.Web.Components;
using Daleel.Web.Conversation;
using Daleel.Web.Data;
using Daleel.Web.Logging;
using Daleel.Web.RateLimiting;
using Daleel.Web.Services;
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
// it can be toggled per environment: it defaults OFF (don't leak stack traces to clients), and is
// currently enabled in appsettings.json to diagnose a production circuit error. Set "DetailedErrors"
// back to false once the root cause is found.
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

// Async conversation backend: SignalR + a queue + a background worker run searches off the request.
builder.Services.AddSignalR();
builder.Services.AddSingleton<Daleel.Web.Conversation.ISearchJobQueue, Daleel.Web.Conversation.SearchJobQueue>();
builder.Services.AddSingleton<Daleel.Web.Conversation.SignalRConversationBroadcaster>();
builder.Services.AddSingleton<Daleel.Web.Conversation.IConversationBroadcaster>(sp => sp.GetRequiredService<Daleel.Web.Conversation.SignalRConversationBroadcaster>());
builder.Services.AddSingleton<Daleel.Web.Conversation.IConversationNotifier>(sp => sp.GetRequiredService<Daleel.Web.Conversation.SignalRConversationBroadcaster>());
builder.Services.AddScoped<Daleel.Web.Conversation.ISearchRunner, Daleel.Web.Conversation.AgentSearchRunner>();
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
}
