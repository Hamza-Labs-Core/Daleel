using System.Text.Json;
using Daleel.Search.Abstractions;
using Daleel.Search.Providers;
using FluentAssertions;
using Xunit;

namespace Daleel.Search.Tests;

public class BrowserSearchProviderTests
{
    /// <summary>A fake edge browser: returns a canned extraction and records the SERP URL it was asked for.</summary>
    private sealed class FakeExtractor : IExtractProvider
    {
        private readonly string _json;
        public FakeExtractor(string json) => _json = json;

        public string Name => "fake-browser";
        public string? LastUrl { get; private set; }

        public Task<JsonElement> ExtractAsync(string url, object jsonSchema, CancellationToken cancellationToken = default)
        {
            LastUrl = url;
            using var doc = JsonDocument.Parse(_json);
            return Task.FromResult(doc.RootElement.Clone());
        }
    }

    private static BrowserSearchProvider Build(string json, string engine = "https://e/?q={q}") =>
        new(new FakeExtractor(json), engine);

    [Fact]
    public async Task ParsesResultsWrapper_MapsFieldsAndPositions()
    {
        const string json = """
        { "results": [
            { "title": "Jarir", "url": "https://jarir.com", "snippet": "electronics store" },
            { "title": "Amazon", "url": "https://amazon.com", "snippet": "shop" }
        ] }
        """;

        var results = await Build(json).SearchAsync(new SearchQuery { Query = "laptops", Kind = SearchKind.Web });

        results.Results.Should().HaveCount(2);
        results.Results[0].Title.Should().Be("Jarir");
        results.Results[0].Url.Should().Be("https://jarir.com");
        results.Results[0].Snippet.Should().Be("electronics store");
        results.Results[0].Source.Should().Be("browser-serp");
        results.Results[0].Position.Should().Be(1);
        results.Results[1].Position.Should().Be(2);
    }

    [Fact]
    public async Task ToleratesBareArray()
    {
        var results = await Build("""[ { "title": "A", "url": "https://a" } ]""")
            .SearchAsync(new SearchQuery { Query = "x", Kind = SearchKind.Web });

        results.Results.Should().ContainSingle();
    }

    [Fact]
    public async Task SkipsRowsWithNeitherTitleNorUrl()
    {
        const string json = """{ "results": [ { "snippet": "orphan ad row" }, { "url": "https://a", "title": "A" } ] }""";

        var results = await Build(json).SearchAsync(new SearchQuery { Query = "x", Kind = SearchKind.Web });

        results.Results.Should().ContainSingle();
        results.Results[0].Url.Should().Be("https://a");
    }

    [Fact]
    public async Task RespectsMaxResults()
    {
        var items = string.Join(",", Enumerable.Range(0, 10)
            .Select(i => $$"""{ "title": "t{{i}}", "url": "https://x/{{i}}" }"""));

        var results = await Build($$"""{ "results": [ {{items}} ] }""")
            .SearchAsync(new SearchQuery { Query = "x", Kind = SearchKind.Web, MaxResults = 3 });

        results.Results.Should().HaveCount(3);
    }

    [Fact]
    public async Task BuildsGeoTargetedUrlFromTemplate()
    {
        var extractor = new FakeExtractor("""{ "results": [] }""");
        var provider = new BrowserSearchProvider(
            extractor, engine: "https://www.bing.com/search?q={q}&cc={cc}&setlang={lang}");

        await provider.SearchAsync(new SearchQuery
        {
            Query = "مكيف", Kind = SearchKind.Web, CountryCode = "jo", LanguageCode = "ar"
        });

        extractor.LastUrl.Should().Contain("cc=jo").And.Contain("setlang=ar");
        extractor.LastUrl.Should().Contain(Uri.EscapeDataString("مكيف"));
    }

    [Fact]
    public async Task UnsupportedKind_ReturnsEmptyWithoutCallingBrowser()
    {
        var extractor = new FakeExtractor("""{ "results": [ { "url": "https://a", "title": "A" } ] }""");
        var provider = new BrowserSearchProvider(extractor, engine: "https://e/?q={q}");

        var results = await provider.SearchAsync(new SearchQuery { Query = "x", Kind = SearchKind.Shopping });

        results.Results.Should().BeEmpty();
        extractor.LastUrl.Should().BeNull("shopping is not a supported kind for the browser-SERP fallback");
    }

    [Fact]
    public void SupportsWebAndNewsOnly()
    {
        var provider = Build("{}");
        provider.Supports(SearchKind.Web).Should().BeTrue();
        provider.Supports(SearchKind.News).Should().BeTrue();
        provider.Supports(SearchKind.Shopping).Should().BeFalse();
        provider.Supports(SearchKind.Maps).Should().BeFalse();
    }
}
