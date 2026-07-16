namespace Daleel.Core.Observability;

/// <summary>
/// Ambient (AsyncLocal) carrier for the CURRENT search's session id — the value sent to OpenRouter
/// as the request's <c>user</c> field so every LLM call a search makes (planner, extraction,
/// relevance, analyst, synthesis, crawl, and the enrichment-drain units that run long after the
/// synchronous workflow) groups under one identifier in the provider's dashboard and abuse tooling.
/// The twin of <see cref="AmbientApiObserver"/>/<see cref="AmbientSearchEvents"/>: AsyncLocal flows
/// down through awaits, <c>Task.WhenAll</c> fan-outs, sub-workflow child scopes, and the background
/// queue, so a session set once at the search entrypoint reaches every call without threading.
/// </summary>
public static class AmbientLlmSession
{
    private static readonly AsyncLocal<string?> Current = new();

    /// <summary>The current search's session id, or null when no search owns this async flow.</summary>
    public static string? SessionId => Current.Value;

    /// <summary>
    /// Sets the session id for the enclosing async scope; restores the prior value on dispose.
    /// Use as <c>using var _ = AmbientLlmSession.Begin($"search-{jobId}");</c>.
    /// </summary>
    public static IDisposable Begin(string? sessionId)
    {
        var prior = Current.Value;
        Current.Value = sessionId;
        return new Restore(prior);
    }

    private sealed class Restore(string? prior) : IDisposable
    {
        public void Dispose() => Current.Value = prior;
    }
}
