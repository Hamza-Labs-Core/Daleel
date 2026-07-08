using System.Text.Json;
using Daleel.Search.Abstractions;
using FluentAssertions;
using Xunit;

namespace Daleel.Search.Tests;

public class ScrapeRouterFallbackTests
{
    /// <summary>A provider that both scrapes and extracts, returning canned results.</summary>
    private sealed class FakeProvider : IScrapeProvider, IExtractProvider
    {
        private readonly string _json;
        private readonly bool _throwExtract;

        public FakeProvider(string name, string json, bool throwExtract = false)
            => (Name, _json, _throwExtract) = (name, json, throwExtract);

        public string Name { get; }

        public Task<ScrapedPage> ScrapeAsync(
            string url, ScrapeFormat format = ScrapeFormat.Markdown, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ScrapedPage { Url = url, Provider = Name, Success = false, Error = "n/a" });

        public Task<JsonElement> ExtractAsync(string url, object jsonSchema, CancellationToken cancellationToken = default)
        {
            if (_throwExtract)
            {
                throw new InvalidOperationException("boom");
            }

            using var doc = JsonDocument.Parse(_json);
            return Task.FromResult(doc.RootElement.Clone());
        }
    }

    // Extraction runs the chain REVERSED (browser-first), so chain [context.dev, cloudflare-browser]
    // tries cloudflare-browser first, then context.dev.
    private static ScrapeRouter Router(List<ScrapeFallback> hops, FakeProvider contextDev, FakeProvider browser) =>
        new(new IScrapeProvider[] { contextDev, browser }, hops.Add);

    [Fact]
    public async Task ExtractAsync_ReportsFallback_WhenBrowserFirstIsEmpty()
    {
        var hops = new List<ScrapeFallback>();
        var router = Router(hops,
            contextDev: new FakeProvider("context.dev", """{ "products": [ { "name": "X" } ] }"""),
            browser: new FakeProvider("cloudflare-browser", """{ "products": [] }"""));

        var result = await router.ExtractAsync("https://store/p", new { });

        result.GetProperty("products").GetArrayLength().Should().Be(1); // served by context.dev
        hops.Should().ContainSingle();
        hops[0].From.Should().Be("cloudflare-browser");
        hops[0].To.Should().Be("context.dev");
    }

    [Fact]
    public async Task ExtractAsync_NoFallback_WhenBrowserFirstHasProducts()
    {
        var hops = new List<ScrapeFallback>();
        var router = Router(hops,
            contextDev: new FakeProvider("context.dev", """{ "products": [ { "name": "Y" } ] }"""),
            browser: new FakeProvider("cloudflare-browser", """{ "products": [ { "name": "X" } ] }"""));

        await router.ExtractAsync("https://store/p", new { });

        hops.Should().BeEmpty();
    }

    [Fact]
    public async Task ExtractAsync_ReportsFallback_OnThrow()
    {
        var hops = new List<ScrapeFallback>();
        var router = Router(hops,
            contextDev: new FakeProvider("context.dev", """{ "products": [ { "name": "Y" } ] }"""),
            browser: new FakeProvider("cloudflare-browser", "{}", throwExtract: true));

        await router.ExtractAsync("https://store/p", new { });

        hops.Should().ContainSingle();
        hops[0].Reason.Should().Contain("boom");
    }
}
