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
}
