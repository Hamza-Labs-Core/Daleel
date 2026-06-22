using Daleel.Web.Components;
using Daleel.Web.Services;
using Microsoft.AspNetCore.HttpOverrides;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

// Razor components with both interactive runtimes (Server for the secret-bearing agent pages,
// WebAssembly available for the Auto runtime).
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents();

// MudBlazor UI services (theme, dialogs, snackbars, popovers).
builder.Services.AddMudServices();

// Daleel intelligence layer.
builder.Services.AddScoped<BrowserStore>();              // localStorage bridge (per circuit)
builder.Services.AddScoped<LayoutState>();               // shared theme + RTL state
builder.Services.AddSingleton<IAgentFactory, AgentFactory>();
builder.Services.AddSingleton<MonitorService>();

// Liveness probe consumed by the Docker HEALTHCHECK and deploy.sh (host :80 -> container :8080).
builder.Services.AddHealthChecks();

// Behind Cloudflare (TLS terminator), so honour X-Forwarded-* to keep redirects/HSTS correct.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // Cloudflare is the trusted upstream; clearing these accepts the forwarded hop.
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

var app = builder.Build();

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
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(Daleel.Web.Client._Imports).Assembly);

app.Run();
