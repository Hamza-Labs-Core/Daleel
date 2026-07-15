using System.Text.Json;
using Daleel.Search.Abstractions;
using Daleel.Search.Http;
using FluentAssertions;
using Xunit;

namespace Daleel.Search.Tests;

public class ScrapeRouterFallbackTests
{
    /// <summary>A provider that both scrapes and extracts, returning canned results.</summary>
    private sealed class FakeProvider : IScrapeProvider, IExtractProvider
    {
        private readonly string _json;
        private readonly Exception? _extractError;

        public FakeProvider(string name, string json, bool throwExtract = false)
            : this(name, json, throwExtract ? new InvalidOperationException("boom") : null)
        {
        }

        private FakeProvider(string name, string json, Exception? extractError)
            => (Name, _json, _extractError) = (name, json, extractError);

        /// <summary>A provider whose extract throws the given exception — used to simulate a CF 422.</summary>
        public static FakeProvider Failing(string name, Exception error) => new(name, "{}", error);

        public string Name { get; }

        /// <summary>Records how many times ExtractAsync ran, so tests can prove a provider was NOT reached.</summary>
        public int ExtractCalls { get; private set; }

        public Task<ScrapedPage> ScrapeAsync(
            string url, ScrapeFormat format = ScrapeFormat.Markdown, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ScrapedPage { Url = url, Provider = Name, Success = false, Error = "n/a" });

        public Task<JsonElement> ExtractAsync(string url, object jsonSchema, CancellationToken cancellationToken = default)
        {
            ExtractCalls++;
            if (_extractError is not null)
            {
                throw _extractError;
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

    // A 422 escaping the browser extractor is a request-shape reject, not an outage. The router must
    // NOT bounce it to the (depleted) Context.dev — doing so turns a recoverable state into an empty
    // harvest. It stops the chain instead, leaving Context.dev untouched.
    [Fact]
    public async Task ExtractAsync_DoesNotFallBackToContextDev_WhenBrowser422s()
    {
        var hops = new List<ScrapeFallback>();
        var contextDev = new FakeProvider("context.dev", """{ "products": [ { "name": "Y" } ] }""");
        var browser = FakeProvider.Failing("cloudflare-browser", new ProviderException("CF 422") { StatusCode = 422 });
        var router = Router(hops, contextDev, browser);

        var result = await router.ExtractAsync("https://store/p", new { });

        browser.ExtractCalls.Should().Be(1);
        contextDev.ExtractCalls.Should().Be(0, "the router must never bounce a 422 to the depleted provider");
        hops.Should().BeEmpty("a 422 stops the chain — it is not reported as a degrade to the next provider");
        result.ValueKind.Should().Be(JsonValueKind.Object);
        result.TryGetProperty("products", out _).Should().BeFalse("no fallback ran, so the result is the empty object");
    }

    // A genuine outage (non-422) still fails over — the 422 guard must not suppress legitimate fallback.
    [Fact]
    public async Task ExtractAsync_StillFallsBack_OnNon422Failure()
    {
        var hops = new List<ScrapeFallback>();
        var contextDev = new FakeProvider("context.dev", """{ "products": [ { "name": "Y" } ] }""");
        var browser = FakeProvider.Failing("cloudflare-browser", new ProviderException("CF down") { StatusCode = 503 });
        var router = Router(hops, contextDev, browser);

        var result = await router.ExtractAsync("https://store/p", new { });

        contextDev.ExtractCalls.Should().Be(1, "a 503 is an outage — fall over to the next extractor");
        result.GetProperty("products").GetArrayLength().Should().Be(1);
        hops.Should().ContainSingle();
    }
}
