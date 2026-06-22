using Daleel.Agent;
using Daleel.Web.Data;
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
    [Inject] protected ICurrentUser CurrentUser { get; set; } = default!;
    [Inject] protected ISearchHistoryRepository History { get; set; } = default!;

    /// <summary>Id of the history row written for the most recent run (null if signed out).</summary>
    protected int? LastHistoryId { get; private set; }

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
    /// <paramref name="work"/>, and surfaces any failure as <see cref="Error"/>. When
    /// <paramref name="recordHistory"/> is supplied it is invoked on success to build a history
    /// row — auto-saved for signed-in users (a no-op for anonymous visitors).
    /// </summary>
    protected async Task RunAsync(
        Func<AgentService, CancellationToken, Task> work,
        Func<SearchHistoryEntry?>? recordHistory = null)
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
        LastHistoryId = null;
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

            if (recordHistory is not null)
            {
                LastHistoryId = await SaveHistoryAsync(recordHistory());
            }
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

    /// <summary>
    /// Persists a history row for the signed-in user. Stamps owner/geo/model/timestamp here so
    /// pages only supply the query-specific fields. Returns the new id, or null when no user is
    /// signed in (anonymous visitors keep full use of the tool, just no saved history).
    /// </summary>
    private async Task<int?> SaveHistoryAsync(SearchHistoryEntry? entry)
    {
        if (entry is null)
        {
            return null;
        }

        var userId = await CurrentUser.GetUserIdAsync();
        if (userId is null)
        {
            return null;
        }

        entry.UserId = userId;
        entry.Geo = Geo;
        entry.Model = Model;
        entry.CreatedAt = DateTimeOffset.UtcNow;
        entry.ResultSummary = Truncate(entry.ResultSummary, 1000);

        var saved = await History.AddAsync(entry);
        return saved.Id;
    }

    /// <summary>Truncates to <paramref name="max"/> characters with an ellipsis.</summary>
    protected static string? Truncate(string? s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";

    private void AppendLog(string line) => InvokeAsync(() =>
    {
        LogLines.Add(line);
        StateHasChanged();
    });
}
