using Daleel.Agent;
using Daleel.Core.Geo;
using Daleel.Core.Models;
using Daleel.Search.Abstractions;
using FluentAssertions;
using Xunit;

namespace Daleel.Agent.Tests;

public class AgentServiceTests
{
    private const string StrategyJson = """
        {
          "queryType": "ProductResearch",
          "subject": "مكيف",
          "webQueries": ["أفضل مكيف في الأردن", "best AC Jordan"],
          "shoppingQueries": ["مكيف سبليت سعر"],
          "socialQueries": ["مكيفات الأردن"],
          "placesQueries": ["متاجر مكيفات"],
          "urlsToRead": [],
          "reasoning": "research the AC category in Jordan"
        }
        """;

    private static readonly DateTimeOffset FixedNow = new(2026, 6, 22, 12, 0, 0, TimeSpan.Zero);

    private static FakeLlmClient PlannerAndAnalyst(string analysis = "Here is the analysis.") =>
        new(system => system == PromptTemplates.PlannerSystem ? StrategyJson : analysis);

    [Fact]
    public async Task PlanAsync_ParsesStrategyFromLlmJson()
    {
        var agent = new AgentService(PlannerAndAnalyst());
        var strategy = await agent.PlanAsync("plan something");

        strategy.QueryType.Should().Be(QueryType.ProductResearch);
        strategy.WebQueries.Should().HaveCount(2);
        strategy.PlacesQueries.Should().Contain("متاجر مكيفات");
    }

    [Fact]
    public async Task PlanAsync_GarbageResponse_ReturnsEmptyStrategy()
    {
        var agent = new AgentService(new FakeLlmClient(_ => "I don't know"));
        var strategy = await agent.PlanAsync("x");
        strategy.QueryType.Should().Be(QueryType.General);
        strategy.WebQueries.Should().BeEmpty();
    }

    [Fact]
    public async Task AskAsync_PlansGathersAndAnalyzes()
    {
        var search = new FakeSearchProvider(
            new SearchResult { Title = "Top ACs", Snippet = "review", Url = "https://x", Kind = SearchKind.Web });
        var llm = PlannerAndAnalyst("The best AC is Samsung AR24.");
        var agent = new AgentService(llm,
            new AgentOptions { DefaultGeo = "jordan", Clock = () => FixedNow },
            search: search);

        var answer = await agent.AskAsync("أفضل مكيف في الأردن", "jordan");

        answer.Geo.Should().Be("jordan");
        answer.QueryType.Should().Be(QueryType.ProductResearch);
        answer.Summary.Should().Be("The best AC is Samsung AR24.");
        answer.Research.WebResults.Should().NotBeEmpty();
        answer.GeneratedAt.Should().Be(FixedNow);

        // Both planner and analyst roles were exercised. Product queries use the product-focused
        // analyst system prompt rather than the generic one.
        llm.SystemPromptsSeen.Should().Contain(PromptTemplates.PlannerSystem);
        llm.SystemPromptsSeen.Should().Contain(PromptTemplates.ProductAnalystSystem);
    }

    [Fact]
    public async Task AskAsync_ProductQuery_ProjectsStructuredListings()
    {
        // FakeSearchProvider returns the same set for every kind; we lean on classification to bucket them.
        var search = new FakeSearchProvider(
            new SearchResult
            {
                Title = "Samsung Split AC AR24", Price = new Money(450, "JOD"), Seller = "OpenSooq",
                Url = "https://jo.opensooq.com/en/listing/12345678", Kind = SearchKind.Shopping
            },
            new SearchResult
            {
                Title = "Air Conditioners | Samsung Jordan",
                Url = "https://www.samsung.com/jo/air-conditioners/", Kind = SearchKind.Web
            },
            new SearchResult
            {
                Title = "Best ACs in Jordan 2024 - Buying Guide",
                Url = "https://blog.example.com/best-acs", Kind = SearchKind.Web
            });

        var agent = new AgentService(PlannerAndAnalyst("Top picks summarized."),
            new AgentOptions { DefaultGeo = "jordan", Clock = () => FixedNow }, search: search);

        var answer = await agent.AskAsync("ACs in Jordan", "jordan");

        answer.Products.Should().NotBeNull();
        var products = answer.Products!;
        products.Geo.Should().Be("jordan");
        products.Country.Should().Be("Jordan");
        products.IncludeInternational.Should().BeFalse();
        // The priced Samsung hit is aggregated into a model carrying that offer.
        products.Models.Should().Contain(m => m.Offers.Any(o => o.Price == 450 && o.Currency == "JOD"));
        products.Brands.Should().Contain(b => b.Name == "Samsung");
        products.Reviews.Should().Contain(r => r.Title.Contains("Best ACs"));
        products.GeneratedAt.Should().Be(FixedNow);
    }

    [Fact]
    public async Task AskAsync_ProductQuery_AssessesBrandReputation()
    {
        const string repJson = """
            { "brands": [ {
              "brand": "Samsung", "score": 4.3, "pros": ["reliable"], "complaints": ["pricey"],
              "hasLocalService": true, "warranty": "2-year local", "summary": "Well supported in-market.",
              "reviews": [
                { "quote": "Works great after 2 years", "source": "Facebook group", "url": "https://fb.com/p/1", "sentiment": "positive", "date": "2026-01-10", "language": "en" },
                { "quote": "Remote stopped working", "source": "Twitter", "sentiment": "negative", "language": "ar", "originalText": "الريموت تعطل" }
              ]
            } ] }
            """;

        var llm = new FakeLlmClient(system =>
            system == PromptTemplates.PlannerSystem ? StrategyJson
            : system == PromptTemplates.BrandReputationSystem ? repJson
            : "summary");

        var search = new FakeSearchProvider(
            new SearchResult
            {
                Title = "Samsung Split AC AR24", Price = new Money(450, "JOD"), Seller = "OpenSooq",
                Url = "https://jo.opensooq.com/en/listing/123", Kind = SearchKind.Shopping
            });

        var agent = new AgentService(llm, new AgentOptions { DefaultGeo = "jordan", Clock = () => FixedNow }, search: search);

        var answer = await agent.AskAsync("ACs in Jordan", "jordan");

        var samsung = answer.Products!.Models.First(m => m.Brand == "Samsung");
        samsung.BrandReputation.Should().NotBeNull();
        samsung.BrandReputation!.Score.Should().Be(4.3);
        samsung.BrandReputation.HasLocalService.Should().BeTrue();
        samsung.BrandReputation.Flag.Should().Be(ReputationFlag.StrongLocalPresence);
        llm.SystemPromptsSeen.Should().Contain(PromptTemplates.BrandReputationSystem);

        // Social proof: real user quotes with sentiment breakdown.
        var social = samsung.BrandReputation.Social;
        social.Should().NotBeNull();
        social!.Reviews.Should().HaveCount(2);
        social.Positive.Should().Be(1);
        social.Negative.Should().Be(1);
        social.Reviews.Should().Contain(r => r.OriginalText == "الريموت تعطل" && r.Sentiment == Sentiment.Negative);
    }

    [Fact]
    public async Task SearchProductsAsync_DropsNonLocalUnlessRequested()
    {
        // A non-local product listing (no country signal in the URL) plus a local one.
        var search = new FakeSearchProvider(
            new SearchResult
            {
                Title = "Local AC deal", Url = "https://shop.jo/product/local-ac", Kind = SearchKind.Web,
                Snippet = "available now 400 JOD"
            },
            new SearchResult
            {
                Title = "Imported AC", Url = "https://global-store.com/product/imported-ac", Kind = SearchKind.Web,
                Snippet = "ships from abroad 600 USD"
            });

        var agent = new AgentService(PlannerAndAnalyst(),
            new AgentOptions { DefaultGeo = "jordan", Clock = () => FixedNow }, search: search);

        var local = await agent.SearchProductsAsync("ACs in Jordan", "jordan");
        // The non-local web listing is dropped; the .jo one is kept.
        local.IncludeInternational.Should().BeFalse();
        local.Models.SelectMany(m => m.Offers).Should()
            .Contain(o => (o.Url ?? "").Contains("shop.jo"))
            .And.NotContain(o => (o.Url ?? "").Contains("global-store.com"));

        var intl = await agent.SearchProductsAsync("ACs in Jordan, show international options too", "jordan");
        intl.IncludeInternational.Should().BeTrue();
        intl.Models.SelectMany(m => m.Offers).Should().Contain(o => (o.Url ?? "").Contains("global-store.com"));
    }

    [Fact]
    public async Task GatherAsync_RunsSearchInMarketLanguageAndCountry()
    {
        var search = new FakeSearchProvider(
            new SearchResult { Title = "r", Kind = SearchKind.Web });
        var agent = new AgentService(PlannerAndAnalyst(), search: search);

        var strategy = await agent.PlanAsync("x");
        var bundle = await agent.GatherAsync(strategy, GeoProfiles.Jordan);

        bundle.WebResults.Should().NotBeEmpty();
        bundle.Sources.Should().NotBeNull();
        search.CallCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task AskAsync_NoProviders_StillReturnsAnswerNotingThinData()
    {
        // No search/places/social/scraper configured: analyst notes the lack of data.
        var llm = new FakeLlmClient(system =>
            system == PromptTemplates.PlannerSystem ? StrategyJson : "should not be called");
        var agent = new AgentService(llm, new AgentOptions { Clock = () => FixedNow });

        var answer = await agent.AskAsync("anything", "jordan");

        // With an empty bundle the agent short-circuits analysis with a stock message.
        answer.Summary.Should().Contain("No research data");
        answer.Research.WebResults.Should().BeEmpty();
    }

    [Fact]
    public async Task ResearchBrandAsync_BuildsReportWithGeoAndSources()
    {
        var search = new FakeSearchProvider(
            new SearchResult { Title = "McDonald's JO", Url = "https://mcd.jo", Kind = SearchKind.Shopping, Seller = "McDonald's" });
        var agent = new AgentService(PlannerAndAnalyst("Brand is strong."),
            new AgentOptions { Clock = () => FixedNow }, search: search);

        var report = await agent.ResearchBrandAsync("ماكدونالدز", "jordan");

        report.Brand.Should().Be("ماكدونالدز");
        report.Geo.Should().Be("jordan");
        report.Summary.Should().Be("Brand is strong.");
        report.GeneratedAt.Should().Be(FixedNow);
    }

    [Fact]
    public async Task FindStoresAsync_NoPlacesProvider_ReturnsEmpty()
    {
        var agent = new AgentService(PlannerAndAnalyst());
        var stores = await agent.FindStoresAsync("مكيفات", "jordan");
        stores.Should().BeEmpty();
    }
}
