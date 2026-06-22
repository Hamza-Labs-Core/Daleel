using Daleel.Search.Abstractions;
using Daleel.Search.Providers;
using FluentAssertions;
using Xunit;

namespace Daleel.Search.Tests;

public class SerpApiProviderTests
{
    private static SerpApiProvider Build(StubHttpMessageHandler handler) =>
        new(apiKey: "k", httpClient: handler.Client(SerpApiProvider.DefaultBaseUrl), delay: (_, _) => Task.CompletedTask);

    [Fact]
    public async Task SearchAsync_Web_ParsesOrganicResults()
    {
        const string json = """
        { "organic_results": [
            { "title": "Best AC in Jordan", "link": "https://a.com", "snippet": "top picks", "position": 1 },
            { "title": "أفضل مكيف", "link": "https://b.com", "snippet": "مراجعات", "position": 2 }
        ] }
        """;
        var provider = Build(new StubHttpMessageHandler(json));

        var results = await provider.SearchAsync(new SearchQuery { Query = "best AC", Kind = SearchKind.Web });

        results.Results.Should().HaveCount(2);
        results.Results[0].Title.Should().Be("Best AC in Jordan");
        results.Results[1].Url.Should().Be("https://b.com");
    }

    [Fact]
    public async Task SearchAsync_Shopping_ParsesPrices()
    {
        const string json = """
        { "shopping_results": [
            { "title": "Samsung AR24", "link": "https://shop/1", "price": "450 JOD", "source": "OpenSooq", "rating": 4.5 }
        ] }
        """;
        var provider = Build(new StubHttpMessageHandler(json));

        var results = await provider.SearchAsync(new SearchQuery { Query = "AC", Kind = SearchKind.Shopping });

        results.Results.Should().ContainSingle();
        var r = results.Results[0];
        r.Price.Should().NotBeNull();
        r.Price!.Value.Amount.Should().Be(450);
        r.Price.Value.Currency.Should().Be("JOD");
        r.Seller.Should().Be("OpenSooq");
    }

    [Fact]
    public async Task SearchAsync_Maps_ParsesLocalResults()
    {
        const string json = """
        { "local_results": [
            { "title": "AC Store Amman", "address": "Downtown", "rating": 4.2, "website": "https://store" }
        ] }
        """;
        var provider = Build(new StubHttpMessageHandler(json));

        var results = await provider.SearchAsync(new SearchQuery { Query = "AC store", Kind = SearchKind.Maps });

        results.Results.Should().ContainSingle();
        results.Results[0].Seller.Should().Be("AC Store Amman");
        results.Results[0].Rating.Should().Be(4.2);
    }

    [Fact]
    public async Task SearchAsync_SendsGeoParams()
    {
        var handler = new StubHttpMessageHandler("""{ "organic_results": [] }""");
        var provider = Build(handler);

        await provider.SearchAsync(new SearchQuery
        {
            Query = "مكيف", Kind = SearchKind.Web, CountryCode = "jo", LanguageCode = "ar"
        });

        var uri = handler.Requests.Single().RequestUri!.ToString();
        uri.Should().Contain("gl=jo").And.Contain("hl=ar").And.Contain("engine=google");
    }

    [Fact]
    public void Supports_AllKinds()
    {
        var provider = Build(new StubHttpMessageHandler("{}"));
        provider.Supports(SearchKind.Web).Should().BeTrue();
        provider.Supports(SearchKind.Shopping).Should().BeTrue();
        provider.Supports(SearchKind.Maps).Should().BeTrue();
    }
}
