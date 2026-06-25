using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

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
}

/// <summary>
/// Broadcaster that fans a state change out two ways: to the SignalR group (external clients) and
/// to the in-process notifier (Blazor circuits). The background worker calls this.
/// </summary>
public sealed class SignalRConversationBroadcaster : IConversationBroadcaster, IConversationNotifier
{
    private readonly IHubContext<ConversationHub> _hub;

    public SignalRConversationBroadcaster(IHubContext<ConversationHub> hub) => _hub = hub;

    public event Action<string, int, string>? Progress;
    public event Action<string, int, string, string?, string?, string?>? Completed;
    public event Action<string, int, string, string>? Enriched;

    public Task ProgressAsync(string userId, int jobId, string message)
    {
        Progress?.Invoke(userId, jobId, message);
        return _hub.Clients.Group(ConversationHub.Group(userId)).SendAsync("Progress", jobId, message);
    }

    public Task CompletedAsync(string userId, int jobId, string status, string? resultJson, string? resultType, string? error)
    {
        Completed?.Invoke(userId, jobId, status, resultJson, resultType, error);
        return _hub.Clients.Group(ConversationHub.Group(userId)).SendAsync("Completed", jobId, status, resultJson, resultType, error);
    }

    public Task EnrichedAsync(string userId, int jobId, string resultJson, string resultType)
    {
        Enriched?.Invoke(userId, jobId, resultJson, resultType);
        return _hub.Clients.Group(ConversationHub.Group(userId)).SendAsync("Enriched", jobId, resultJson, resultType);
    }
}
