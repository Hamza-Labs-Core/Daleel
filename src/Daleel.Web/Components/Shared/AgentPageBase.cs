using Daleel.Agent;
using Daleel.Web.Services;
using Microsoft.AspNetCore.Components;

namespace Daleel.Web.Components.Shared;

/// <summary>
/// Base for the agent-backed feature pages. Centralizes the repeated plumbing: pulling the
/// visitor's geo/model/keys out of <c>localStorage</c> on first render, building an agent with a
/// live progress callback, streaming that progress to the UI, and capturing errors uniformly.
/// </summary>
/// <remarks>
/// Pages derived from this run under <c>@rendermode InteractiveServer</c>, so the agent (and the
/// API keys it needs) executes on the server over the live circuit. The <see cref="AppendLog"/>
/// callback marshals provider progress back onto the render thread via <c>InvokeAsync</c>.
/// </remarks>
public abstract class AgentPageBase : ComponentBase
{
    [Inject] protected IAgentFactory Agents { get; set; } = default!;
    [Inject] protected BrowserStore Store { get; set; } = default!;

    /// <summary>BYO keys read from the browser; merged with server env by the factory.</summary>
    protected Dictionary<string, string> Keys { get; private set; } = new();

    /// <summary>Currently selected market.</summary>
    protected string Geo { get; set; } = "jordan";

    /// <summary>Currently selected OpenRouter model id.</summary>
    protected string Model { get; set; } = Catalog.DefaultModel;

    /// <summary>True while an agent call is in flight.</summary>
    protected bool Busy { get; private set; }

    /// <summary>True once settings have been hydrated from the browser (first render done).</summary>
    protected bool Ready { get; private set; }

    /// <summary>Streaming progress lines from the agent's <c>Log</c> callback.</summary>
    protected List<string> LogLines { get; } = new();

    /// <summary>Last error message, if any.</summary>
    protected string? Error { get; set; }

    /// <summary>Whether an LLM key is resolvable (server env or BYO).</summary>
    protected bool HasLlm => Agents.HasLlm(Keys);

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
        {
            return;
        }

        Keys = await Store.GetKeysAsync();
        Geo = await Store.GetAsync("geo") ?? Geo;
        Model = await Store.GetAsync("model") ?? Model;
        Ready = true;
        await OnReadyAsync();
        StateHasChanged();
    }

    /// <summary>Hook for derived pages to run once settings are loaded (e.g. prefill from query string).</summary>
    protected virtual Task OnReadyAsync() => Task.CompletedTask;

    /// <summary>
    /// Runs an agent operation: builds an agent wired to the progress log, executes
    /// <paramref name="work"/>, and surfaces any failure as <see cref="Error"/>.
    /// </summary>
    protected async Task RunAsync(Func<AgentService, CancellationToken, Task> work)
    {
        if (Busy)
        {
            return;
        }

        if (!Agents.HasLlm(Keys))
        {
            Error = "No LLM key configured. Add one on the Settings page, or set OPENROUTER_API_KEY on the server.";
            return;
        }

        Busy = true;
        Error = null;
        LogLines.Clear();
        StateHasChanged();

        try
        {
            var agent = Agents.Build(new AgentRequest
            {
                Geo = Geo,
                Model = Model,
                Keys = Keys,
                Log = AppendLog
            });

            await work(agent, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            Busy = false;
            StateHasChanged();
        }
    }

    private void AppendLog(string line) => InvokeAsync(() =>
    {
        LogLines.Add(line);
        StateHasChanged();
    });
}
