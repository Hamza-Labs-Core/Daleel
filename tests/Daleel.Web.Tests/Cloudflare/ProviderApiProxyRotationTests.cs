using Daleel.Agent;
using Daleel.Core.Llm;
using Daleel.Search.Providers;
using Daleel.Web.Services;
using FluentAssertions;
using Xunit;

namespace Daleel.Web.Tests.Cloudflare;

/// <summary>
/// FIX 4(b): the proxied GooglePlacesProvider caches its SearchProxyClient with the bearer baked into
/// DefaultRequestHeaders. Keyed by a constant, it was never rebuilt on rotation, so after the app's
/// bearer rotated the cached client kept presenting a stale token the search-worker rejected (401)
/// until process restart. The cache key must include the live CF_SEARCH_WORKER_TOKEN so a rotation
/// rebuilds the client with the fresh bearer.
/// </summary>
public class ProviderApiProxyRotationTests
{
    /// <summary>Minimal IAgentFactory: only Resolve is exercised by Places(); the rest never runs here.</summary>
    private sealed class ResolverFactory : IAgentFactory
    {
        public Dictionary<string, string?> Values { get; } = new();

        public string? Resolve(string name) => Values.TryGetValue(name, out var v) ? v : null;

        public bool HasLlm() => throw new NotSupportedException();
        public ProviderStatus Describe() => throw new NotSupportedException();
        public AgentService Build(AgentRequest request) => throw new NotSupportedException();
        public ILlmClient? TryBuildLlm(string? model = null) => throw new NotSupportedException();
    }

    private static ResolverFactory ProxiedFactory() => new()
    {
        Values =
        {
            // No direct Places key ⇒ the proxied path; a resolvable proxy URL + bearer.
            ["GOOGLE_PLACES_API_KEY"] = null,
            ["CF_SEARCH_WORKER_URL"] = "https://search.test",
            ["CF_SEARCH_WORKER_TOKEN"] = "bearer-v1"
        }
    };

    [Fact]
    public void Proxied_places_provider_is_cached_while_the_bearer_is_unchanged()
    {
        var factory = ProxiedFactory();
        var api = new ProviderApi(factory);

        var first = api.Places();
        var second = api.Places();

        first.Should().NotBeNull("a resolvable proxy configures the Places provider even without a direct key");
        second.Should().BeSameAs(first, "an unchanged bearer must reuse the cached client — no needless rebuild");
    }

    [Fact]
    public void Proxied_places_provider_rebuilds_when_the_bearer_rotates()
    {
        var factory = ProxiedFactory();
        var api = new ProviderApi(factory);

        var before = api.Places();
        before.Should().NotBeNull();

        // The token authority rotates the search-worker bearer at runtime.
        factory.Values["CF_SEARCH_WORKER_TOKEN"] = "bearer-v2";
        var after = api.Places();

        after.Should().NotBeSameAs(before,
            "a rotated bearer must rebuild the proxied client — the stale baked-in token would 401 forever otherwise");
    }
}
