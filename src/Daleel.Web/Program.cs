using Daleel.Web.Components;
using Daleel.Web.Services;
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

var app = builder.Build();

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
