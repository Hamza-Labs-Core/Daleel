using Daleel.Web.Conversation;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Xunit;

namespace Daleel.Web.Tests.Conversation;

/// <summary>
/// Covers the in-memory "latest progress" cache the broadcaster keeps so a page that loads/reloads
/// mid-search can seed its stepper with the search's current stage (issue: blank progress on reload).
/// </summary>
public class ProgressSeedTests
{
    private static SignalRConversationBroadcaster NewBroadcaster() =>
        new(new NoopHubContext());

    [Fact]
    public async Task LatestProgress_ReturnsMostRecentSignal_ForTheRunningJob()
    {
        var b = NewBroadcaster();

        await b.ProgressAsync("u1", jobId: 5, "step-1");
        await b.ProgressAsync("u1", jobId: 5, "step-2");

        // A reconnecting device seeds from the freshest stage, not the first one.
        b.LatestProgress("u1", 5).Should().Be("step-2");
    }

    [Fact]
    public async Task LatestProgress_IsScopedToTheCallersJobAndUser()
    {
        var b = NewBroadcaster();
        await b.ProgressAsync("u1", jobId: 5, "step-2");

        b.LatestProgress("u1", 9).Should().BeNull();   // different job — no stale seed
        b.LatestProgress("u2", 5).Should().BeNull();   // different user — isolation
    }

    [Fact]
    public async Task Completion_ClearsCachedProgress_SoReloadSeedsFromTheResult()
    {
        var b = NewBroadcaster();
        await b.ProgressAsync("u1", jobId: 5, "step-2");

        await b.CompletedAsync("u1", jobId: 5, "completed", "{}", "ask", null);

        b.LatestProgress("u1", 5).Should().BeNull();
    }

    // Minimal IHubContext stand-in: the broadcaster fans out to SignalR groups as a side effect, which
    // is irrelevant to the cache under test — so every send is a no-op.
    private sealed class NoopHubContext : IHubContext<ConversationHub>
    {
        public IHubClients Clients { get; } = new NoopClients();
        public IGroupManager Groups { get; } = new NoopGroups();

        private sealed class NoopClients : IHubClients
        {
            private static readonly IClientProxy Proxy = new NoopProxy();
            public IClientProxy All => Proxy;
            public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => Proxy;
            public IClientProxy Client(string connectionId) => Proxy;
            public IClientProxy Clients(IReadOnlyList<string> connectionIds) => Proxy;
            public IClientProxy Group(string groupName) => Proxy;
            public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => Proxy;
            public IClientProxy Groups(IReadOnlyList<string> groupNames) => Proxy;
            public IClientProxy User(string userId) => Proxy;
            public IClientProxy Users(IReadOnlyList<string> userIds) => Proxy;
        }

        private sealed class NoopProxy : IClientProxy
        {
            public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) =>
                Task.CompletedTask;
        }

        private sealed class NoopGroups : IGroupManager
        {
            public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) =>
                Task.CompletedTask;
            public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) =>
                Task.CompletedTask;
        }
    }
}
