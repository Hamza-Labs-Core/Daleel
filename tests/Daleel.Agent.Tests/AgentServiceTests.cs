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

        // Both planner and analyst roles were exercised.
        llm.SystemPromptsSeen.Should().Contain(PromptTemplates.PlannerSystem);
        llm.SystemPromptsSeen.Should().Contain(PromptTemplates.AnalystSystem);
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
