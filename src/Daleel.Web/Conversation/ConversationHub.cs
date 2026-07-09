using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Daleel.Web.Pipeline;

namespace Daleel.Web.Conversation;

/// <summary>
/// Real-time channel between the server and a user's devices. Each connection joins a group named
/// by the user id, so a search running on the phone streams progress to the laptop too.
/// </summary>
[Authorize]
public sealed class ConversationHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        var userId = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is not null)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, Group(userId));
        }

        await base.OnConnectedAsync();
    }

    public static string Group(string userId) => $"user:{userId}";
}

/// <summary>Pushes conversation state changes to all of a user's connected devices.</summary>
public interface IConversationBroadcaster
{
    Task ProgressAsync(string userId, int jobId, string message);
    Task CompletedAsync(string userId, int jobId, string status, string? resultJson, string? resultType, string? error);

    /// <summary>A refreshed result for an already-completed job (item deep-dive specs arrived) — the UI
    /// re-renders in place. Separate from Completed so the "done" state isn't reset to "running".</summary>
    Task EnrichedAsync(string userId, int jobId, string resultJson, string resultType);

    /// <summary>
    /// An intermediate result for a job that is STILL RUNNING — the UI renders the grid immediately and
    /// keeps the search-ongoing affordance ("results load as they're ready"). The pipeline pushes one as
    /// soon as products are extracted and again (throttled) as each enrichment sub-workflow lands, so a
    /// user never stares at a stepper while finished data sits in memory.
    /// </summary>
    Task PartialAsync(string userId, int jobId, string resultJson, string resultType);
}

/// <summary>
/// In-process pub/sub so interactive Blazor Server circuits (every device of a user runs one on
/// this server) update live without each acting as its own SignalR client. The SignalR hub covers
/// external/mobile clients; this covers the in-app UI.
/// </summary>
public interface IConversationNotifier
{
    event Action<string, int, string>? Progress;
    event Action<string, int, string, string?, string?, string?>? Completed;

    /// <summary>(userId, jobId, resultJson, resultType) — a streamed result refresh after completion.</summary>
    event Action<string, int, string, string>? Enriched;

    /// <summary>(userId, jobId, resultJson, resultType) — an intermediate result while still running.</summary>
    event Action<string, int, string, string>? Partial;

    /// <summary>
    /// The most recent progress signal for the user's running <paramref name="jobId"/>, or null when
    /// none is cached (no active job, a different job, or the job already finished). Lets a page that
    /// loads/reloads mid-search seed its stepper with the search's current stage instead of starting
    /// blank and waiting for the next live broadcast.
    /// </summary>
    string? LatestProgress(string userId, int jobId);
}

/// <summary>
/// Broadcaster that fans a state change out two ways: to the SignalR group (external clients) and
/// to the in-process notifier (Blazor circuits). The background worker calls this.
/// </summary>
public sealed class SignalRConversationBroadcaster : IConversationBroadcaster, IConversationNotifier
{
    private readonly IHubContext<ConversationHub> _hub;

    public SignalRConversationBroadcaster(IHubContext<ConversationHub> hub) => _hub = hub;

    // Latest progress signal per (user, job), so a device that loads/reloads mid-search can seed its
    // stepper with the job's current stage rather than waiting for the next live broadcast. Keyed by
    // BOTH ids: jobs run concurrently now, so two of one user's searches must not clobber each other's
    // cached signal (and completing job A must not wipe job B's). Held in memory on purpose: the job
    // queue is itself in-process (a restart loses running jobs), so this shares that durability model —
    // and a lost entry just degrades to the first-stage seed, never an error.
    private readonly ConcurrentDictionary<(string UserId, int JobId), string> _latestProgress = new();

    public event Action<string, int, string>? Progress;
    public event Action<string, int, string, string?, string?, string?>? Completed;
    public event Action<string, int, string, string>? Enriched;
    public event Action<string, int, string, string>? Partial;

    public string? LatestProgress(string userId, int jobId) =>
        _latestProgress.TryGetValue((userId, jobId), out var signal) ? signal : null;

    public Task ProgressAsync(string userId, int jobId, string message)
    {
        _latestProgress[(userId, jobId)] = message;
        Progress?.Invoke(userId, jobId, message);
        // External SignalR subscribers (off-device/mobile clients) get a wire-safe copy: the internal
        // localization key is stripped so pipeline internals (e.g. "Progress.Msg.ScrapingBrandCatalog")
        // never travel off-device — only the step + user-facing args remain. Plain, non-encoded agent
        // diagnostic lines aren't broadcast at all, matching the in-app UI (which ignores them). The
        // in-process arm above still carries the full signal, so server-side localization is unaffected.
        if (!SearchProgressSignal.TryDecode(message, out var signal))
        {
            return Task.CompletedTask;
        }
        return _hub.Clients.Group(ConversationHub.Group(userId))
            .SendAsync("Progress", jobId, SearchProgressSignal.EncodeWireSafe(signal));
    }

    public Task CompletedAsync(string userId, int jobId, string status, string? resultJson, string? resultType, string? error)
    {
        // THIS job is finished — drop its cached progress so a later page load seeds from the completed
        // result, never a stale "running" stage. Other concurrent jobs' entries are untouched.
        _latestProgress.TryRemove((userId, jobId), out _);
        Completed?.Invoke(userId, jobId, status, resultJson, resultType, error);
        return _hub.Clients.Group(ConversationHub.Group(userId)).SendAsync("Completed", jobId, status, resultJson, resultType, error);
    }

    public Task EnrichedAsync(string userId, int jobId, string resultJson, string resultType)
    {
        Enriched?.Invoke(userId, jobId, resultJson, resultType);
        return _hub.Clients.Group(ConversationHub.Group(userId)).SendAsync("Enriched", jobId, resultJson, resultType);
    }

    public Task PartialAsync(string userId, int jobId, string resultJson, string resultType)
    {
        // Deliberately does NOT touch _latestProgress: the job is still running and the stepper
        // should keep showing its live stage alongside the partial grid.
        Partial?.Invoke(userId, jobId, resultJson, resultType);
        return _hub.Clients.Group(ConversationHub.Group(userId)).SendAsync("Partial", jobId, resultJson, resultType);
    }
}
