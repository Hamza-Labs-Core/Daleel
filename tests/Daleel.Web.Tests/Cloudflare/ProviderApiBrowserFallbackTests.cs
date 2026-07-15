using Daleel.Agent;
using Daleel.Core.Llm;
using Daleel.Search.Abstractions;
using Daleel.Web.Services;
using FluentAssertions;
using Xunit;

namespace Daleel.Web.Tests.Cloudflare;

/// <summary>
/// The enrichment drain's page fetch (<see cref="ProviderApi.ScrapePageAsync"/>) used to be
/// Context.dev-only (edge, then inline) — when Context.dev's credits depleted, EVERY
/// enrich.verifypage unit died with "page fetch returned nothing" (QA: 117 dead, 0 completed,
/// for a week), so no post-crawl price/image ever attached. The fix mirrors the pipeline's
/// ScrapeRouter: Cloudflare Browser Rendering is the last-resort scrape when Context.dev is
/// unavailable or failed.
/// </summary>
public class ProviderApiBrowserFallbackTests
{
    private sealed class ResolverFactory : IAgentFactory
    {
        public Dictionary<string, string?> Values { get; } = new();

        public string? Resolve(string name) => Values.TryGetValue(name, out var v) ? v : null;

        public bool HasLlm() => throw new NotSupportedException();
        public ProviderStatus Describe() => throw new NotSupportedException();
        public AgentService Build(AgentRequest request) => throw new NotSupportedException();
        public ILlmClient? TryBuildLlm(string? model = null) => throw new NotSupportedException();
    }

    private sealed class FakeBrowser : IScrapeProvider
    {
        public int Calls { get; private set; }
        public string Name => "fake-browser";

        public Task<ScrapedPage> ScrapeAsync(
            string url, ScrapeFormat format = ScrapeFormat.Markdown, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new ScrapedPage
            {
                Url = url,
                Content = "# rendered by browser",
                Success = true,
                Provider = Name
            });
        }
    }

    [Fact]
    public async Task ScrapePage_FallsBackToBrowser_WhenContextDevIsUnavailable()
    {
        // No CONTEXT_DEV_API_KEY and no edge worker — before the fix this returned null immediately.
        var factory = new ResolverFactory();
        var browser = new FakeBrowser();
        var api = new ProviderApi(factory, browserFallback: browser);

        var page = await api.ScrapePageAsync("https://store.example/product/1");

        page.Should().NotBeNull("the browser renderer is the last-resort scrape when Context.dev is gone");
        page!.Content.Should().Contain("rendered by browser");
        browser.Calls.Should().Be(1);
    }

    [Fact]
    public async Task ScrapePage_ReturnsNull_WhenNothingIsConfigured()
    {
        var api = new ProviderApi(new ResolverFactory());
        (await api.ScrapePageAsync("https://store.example/p")).Should().BeNull();
    }
}
