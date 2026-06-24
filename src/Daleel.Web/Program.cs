using Daleel.Web.Auth;
using Daleel.Web.Components;
using Daleel.Web.Conversation;
using Daleel.Web.Data;
using Daleel.Web.RateLimiting;
using Daleel.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

// Razor components with both interactive runtimes (Server for the secret-bearing agent pages,
// WebAssembly available for the Auto runtime).
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents();

// MudBlazor UI services (theme, dialogs, snackbars, popovers).
builder.Services.AddMudServices();

// ── Localization (English + Arabic, cookie-based) ───────────────────────────
builder.Services.AddLocalization(o => o.ResourcesPath = "Resources");
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

// ── Identity + external authentication ───────────────────────────────────────
var authentication = builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = IdentityConstants.ApplicationScheme;
    options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
});
authentication.AddIdentityCookies();
authentication.AddExternalProviders(builder.Configuration);

// Unauthenticated visitors are sent to /login instead of a 404 when a [Authorize] page is hit —
// except API/hub calls, which get a clean 401/403 instead of an HTML redirect.
builder.Services.ConfigureApplicationCookie(o =>
{
    o.LoginPath = "/login";
    o.AccessDeniedPath = "/login";
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
        options.SignIn.RequireConfirmedAccount = false; // external providers vouch for identity
        options.User.RequireUniqueEmail = false;        // some providers don't return email
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
builder.Services.AddScoped<LayoutState>();               // shared theme + RTL state
builder.Services.AddScoped<ICurrentUser, CurrentUser>(); // authenticated id, from the circuit
builder.Services.AddScoped<ISearchHistoryRepository, SearchHistoryRepository>();
builder.Services.AddScoped<ISavedResultRepository, SavedResultRepository>();
builder.Services.AddScoped<IQuotaService, QuotaService>();
builder.Services.AddScoped<IAnalyticsService, AnalyticsService>();
builder.Services.AddScoped<ISystemConfigService, SystemConfigService>();
builder.Services.AddScoped<IApiCallLogRepository, ApiCallLogRepository>();
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

// IP rate limiting (in-memory fixed-window — no Redis at this scale).
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<IIpRateLimiter, IpRateLimiter>();
builder.Services.AddSingleton<IAgentFactory, AgentFactory>();
builder.Services.AddScoped<IModelDetailService, ModelDetailService>();
builder.Services.AddSingleton<MonitorService>();

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
