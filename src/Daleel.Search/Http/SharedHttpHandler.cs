namespace Daleel.Search.Http;

/// <summary>
/// A process-wide, shared <see cref="SocketsHttpHandler"/> for all HTTP-backed providers and LLM
/// clients. Every provider builds a thin <see cref="HttpClient"/> over this one handler
/// (<c>disposeHandler: false</c>), so they all share a single pooled set of TCP connections instead
/// of each allocating — and leaking — its own.
/// </summary>
/// <remarks>
/// This is the lightweight stand-in for <c>IHttpClientFactory</c> in a library that is constructed
/// manually (by the CLI composition root and the web <c>AgentFactory</c>) rather than via DI.
/// <see cref="SocketsHttpHandler.PooledConnectionLifetime"/> bounds how long a pooled connection is
/// reused, so DNS changes are picked up — the classic failure mode of a long-lived singleton client.
/// </remarks>
public static class SharedHttpHandler
{
    /// <summary>The shared handler. Never disposed — it lives for the lifetime of the process.</summary>
    public static SocketsHttpHandler Instance { get; } = new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        MaxConnectionsPerServer = 32
    };

    /// <summary>
    /// Builds a pooled <see cref="HttpClient"/> over the shared handler. The returned client owns no
    /// sockets of its own, so it is safe to leave undisposed (the handler is not disposed with it).
    /// </summary>
    public static HttpClient CreateClient() => new(Instance, disposeHandler: false);
}
