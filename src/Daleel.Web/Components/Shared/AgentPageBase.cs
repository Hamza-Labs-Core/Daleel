using System.Diagnostics;
using Daleel.Agent;
using Daleel.Web.Data;
using Daleel.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using MudBlazor;

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
    [Inject] protected IQuotaService Quota { get; set; } = default!;
    [Inject] protected IAnalyticsService Analytics { get; set; } = default!;
    [Inject] protected IDialogService Dialogs { get; set; } = default!;
    [Inject] protected NavigationManager Nav { get; set; } = default!;
    [Inject] protected ILogger<AgentPageBase> Logger { get; set; } = default!;

    /// <summary>Id of the history row written for the most recent run (null if signed out).</summary>
    protected int? LastHistoryId { get; private set; }

    /// <summary>The current user's quota snapshot (null until loaded / when anonymous).</summary>
    protected QuotaStatus? Quota_Status { get; private set; }

    /// <summary>True when the user may run another search (quota not exhausted).</summary>
    protected bool CanSearch => Quota_Status?.CanSearch ?? true;

    /// <summary>BYO keys read from the browser; merged with server env by the factory.</summary>

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
    protected bool HasLlm => Agents.HasLlm();

    /// <summary>Halal filter strictness, hydrated from the browser (default Strict).</summary>
    protected Daleel.Core.Moderation.FilterStrictness Strictness { get; private set; } =
        Daleel.Core.Moderation.FilterStrictness.Strict;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
        {
            return;
        }

        Geo = await Store.GetAsync("geo") ?? Geo;
        Model = await Store.GetAsync("model") ?? Model;
        Strictness = await ResolveStrictnessAsync();
        await RefreshQuotaAsync();
        Ready = true;
        await OnReadyAsync();
        StateHasChanged();
    }

    /// <summary>Reloads the quota snapshot for the signed-in user (no-op when anonymous).</summary>
    protected async Task RefreshQuotaAsync()
    {
        var userId = await CurrentUser.GetUserIdAsync();
        Quota_Status = userId is null
            ? null
            : await Quota.GetStatusAsync(userId, await CurrentUser.IsAdminAsync());
    }

    /// <summary>
    /// Reads the visitor's filter preference, but never trusts the browser to disable filtering:
    /// "Off" is honored only for admins (enforced here, server-side).
    /// </summary>
    private async Task<Daleel.Core.Moderation.FilterStrictness> ResolveStrictnessAsync()
    {
        var pref = await Store.GetAsync("filter.strictness");
        var requested = pref switch
        {
            "off" => Daleel.Core.Moderation.FilterStrictness.Off,
            "moderate" => Daleel.Core.Moderation.FilterStrictness.Moderate,
            _ => Daleel.Core.Moderation.FilterStrictness.Strict
        };

        if (requested == Daleel.Core.Moderation.FilterStrictness.Off && !await CurrentUser.IsAdminAsync())
        {
            return Daleel.Core.Moderation.FilterStrictness.Strict;
        }

        return requested;
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

        // Registration gate: searching requires an account (test-match/dry-run stay public elsewhere).
        var userId = await CurrentUser.GetUserIdAsync();
        if (userId is null)
        {
            await PromptSignInAsync();
            return;
        }

        if (!Agents.HasLlm())
        {
            Error = "No LLM key configured. Add one on the Settings page, or set OPENROUTER_API_KEY on the server.";
            return;
        }

        // Per-user monthly credits (reads the user's plan allowance; admins bypass). The actual cost is
        // charged after the run below, once the providers it called are known.
        var isAdmin = await CurrentUser.IsAdminAsync();
        Quota_Status = await Quota.GetStatusAsync(userId, isAdmin);
        if (!Quota_Status.CanSearch)
        {
            Error = $"You're out of credits for this month ({Quota_Status.Limit} included). Upgrade for more.";
            StateHasChanged();
            return;
        }

        Busy = true;
        Error = null;
        LastHistoryId = null;
        LogLines.Clear();
        StateHasChanged();

        // Meter the provider calls this search makes so we can charge its real credit cost afterwards.
        var meter = new Daleel.Web.Conversation.JobApiCallCollector(_ => { }, 0m, null);
        try
        {
            var agent = Agents.Build(new AgentRequest
            {
                Geo = Geo,
                Model = Model,
                Log = AppendLog,
                Strictness = Strictness,
                ApiObserver = meter
            });

            var sw = Stopwatch.StartNew();
            await work(agent, CancellationToken.None);
            sw.Stop();

            await Quota.ChargeCreditsAsync(userId, meter.TotalCredits);
            Quota_Status = await Quota.GetStatusAsync(userId, isAdmin);

            if (recordHistory is not null)
            {
                var entry = recordHistory();
                LastHistoryId = await SaveHistoryAsync(entry);
                await RecordSearchAnalyticsAsync(entry, agent, userId, (int)sw.ElapsedMilliseconds);
            }
        }
        catch (Exception ex)
        {
            // Log the full exception server-side; show the user a generic message so internal detail
            // (provider hostnames, partial keys in URLs, SQL text) never leaks to the browser.
            Logger.LogError(ex, "Agent run failed for user {UserId}", userId);
            Error = "Something went wrong while running your search. Please try again.";
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

    /// <summary>Records an analytics row for a completed search, including how many results were filtered.</summary>
    private async Task RecordSearchAnalyticsAsync(SearchHistoryEntry? entry, AgentService agent, string userId, int durationMs)
    {
        if (entry is null)
        {
            return;
        }

        var audit = agent.ContentFilter.AuditLog;
        var categories = audit
            .Select(a => a.Contains(':') ? a[(a.IndexOf(':') + 1)..] : a)
            .Distinct();

        await Analytics.RecordSearchAsync(new AnalyticsEvent
        {
            // Anonymous analytics: store a one-way hash of the user id, never the id itself.
            UserId = Daleel.Web.Data.Anonymizer.HashUserId(userId),
            Query = Truncate(entry.Query, 2000),
            QueryType = entry.QueryType,
            Geo = Geo,
            Model = Model,
            DurationMs = durationMs,
            FilteredCount = audit.Count,
            FilteredCategories = string.Join(",", categories)
        });
    }

    /// <summary>Shows a "sign in to search" modal and routes to login (with return URL) on accept.</summary>
    private async Task PromptSignInAsync()
    {
        var result = await Dialogs.ShowMessageBox(
            "Sign in to search",
            "Create a free account to run searches — you get 500 free credits every month.",
            yesText: "Sign in", cancelText: "Cancel");

        if (result == true)
        {
            var returnUrl = "/" + Nav.ToBaseRelativePath(Nav.Uri);
            // forceLoad: true — /login's antiforgery cookie must be set on a full document load, not an
            // enhanced-navigation fetch, or the first sign-in POST fails antiforgery (see RedirectToLogin).
            Nav.NavigateTo($"/login?returnUrl={Uri.EscapeDataString(returnUrl)}", forceLoad: true);
        }
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
