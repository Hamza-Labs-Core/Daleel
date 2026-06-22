using Daleel.Web.Auth;
using Daleel.Web.Components;
using Daleel.Web.Data;
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

// Unauthenticated visitors are sent to /login instead of a 404 when a [Authorize] page is hit.
builder.Services.ConfigureApplicationCookie(o =>
{
    o.LoginPath = "/login";
    o.AccessDeniedPath = "/login";
});

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
builder.Services.AddSingleton<IAgentFactory, AgentFactory>();
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

app.UseAuthentication();
app.UseAuthorization();

app.UseAntiforgery();

app.MapAuthEndpoints();

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
}
