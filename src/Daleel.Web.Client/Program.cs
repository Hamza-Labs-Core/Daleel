using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MudBlazor.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// MudBlazor services for any component that runs client-side under the Auto runtime.
builder.Services.AddMudServices();

await builder.Build().RunAsync();
