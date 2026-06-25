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
    public async Task AskAsync_ProductQuery_ExtractsModelsFromProseWhenDeterministicParsersFindNothing()
    {
        // The real-world failure: web search returns only a buying-guide article and a brand
        // homepage — nothing the deterministic parsers turn into a priced listing (no shopping
        // rows, no ProductListing classification). The LLM extraction pass reads the prose and
        // produces the structured products, so the grid is populated instead of empty.
        var search = new FakeSearchProvider(
            new SearchResult
            {
                Title = "Best ACs in Jordan 2026 - Buying Guide",
                Snippet = "Samsung WindFree ~320 JOD at Smart Buy; LG DualCool ~410 JOD.",
                Url = "https://blog.example.com/best-acs", Kind = SearchKind.Web
            },
            new SearchResult
            {
                Title = "Air Conditioners | Samsung Jordan",
                Url = "https://www.samsung.com/jo/air-conditioners/", Kind = SearchKind.Web
            });

        const string productsJson = """
            { "products": [
              { "name": "Samsung WindFree 1.5 ton Split AC", "brand": "Samsung", "model": "AR18TXHQ",
                "specs": { "capacity": 1.5, "energy": "A++" },
                "offers": [ { "source": "Smart Buy", "price": "320 JOD", "currency": "JOD",
                              "url": "https://smartbuy.com.jo/p/ar18", "condition": "New " } ] },
              { "name": "LG DualCool 2 ton", "brand": "LG", "model": "S4-Q24",
                "offers": [ { "source": "Leaders", "price": 410, "currency": "JOD",
                              "url": "https://leaders.jo/p/s4q24" } ] }
            ] }
            """;

        var llm = new FakeLlmClient(system =>
            system == PromptTemplates.PlannerSystem ? StrategyJson
            : system == PromptTemplates.ProductExtractionSystem ? productsJson
            : "Top picks summarized.");

        var agent = new AgentService(llm,
            new AgentOptions { DefaultGeo = "jordan", Clock = () => FixedNow }, search: search);

        var answer = await agent.AskAsync("best ACs in Jordan", "jordan");

        var products = answer.Products!;
        products.ProductCount.Should().Be(2);
        // Tolerant parsing: the "320 JOD" string price and the numeric spec both survive.
        products.Models.Should().Contain(m => m.Brand == "Samsung"
            && m.Specs.ContainsKey("capacity")
            && m.Offers.Any(o => o.Price == 320 && o.Currency == "JOD" && (o.Url ?? "").Contains("smartbuy")));
        // The non-canonical "New " condition is normalized to "new" on the extracted offer.
        products.Models.SelectMany(m => m.Offers)
            .Should().Contain(o => (o.Url ?? "").Contains("smartbuy") && o.Condition == "new");
        products.Models.Should().Contain(m => m.Brand == "LG" && m.Offers.Any(o => o.Price == 410));
        // Brands are derived from the extracted models, so they populate too.
        products.Brands.Should().Contain(b => b.Name == "Samsung");
        products.Brands.Should().Contain(b => b.Name == "LG");
        llm.SystemPromptsSeen.Should().Contain(PromptTemplates.ProductExtractionSystem);
    }

    [Fact]
    public async Task AskAsync_ProductQuery_PopulatesBrandModelsPriceRangeAndModelInsights()
    {
        // Two Samsung models at different prices plus an LG one, each with a distilled verdict —
        // the brand card should list the model names with a price range, and the verdict should
        // ride along on the aggregated model (so the grid card can show it without a deep scrape).
        var search = new FakeSearchProvider(
            new SearchResult { Title = "AC guide", Snippet = "options", Url = "https://blog.x/acs", Kind = SearchKind.Web });

        const string productsJson = """
            { "products": [
              { "name": "Samsung WindFree 1.5 ton", "brand": "Samsung", "model": "AR18TXHQ",
                "offers": [ { "source": "Smart Buy", "price": 320, "currency": "JOD", "url": "https://shop.jo/ar18" } ],
                "pros": ["very quiet", "energy efficient"], "cons": ["pricey filters"],
                "summary": "A quiet, efficient pick for mid-size rooms." },
              { "name": "Samsung WindFree 2 ton", "brand": "Samsung", "model": "AR24TXHQ",
                "offers": [ { "source": "Leaders", "price": 480, "currency": "JOD", "url": "https://shop.jo/ar24" } ] },
              { "name": "LG DualCool", "brand": "LG", "model": "S4-Q24",
                "offers": [ { "source": "Leaders", "price": 410, "currency": "JOD", "url": "https://shop.jo/s4q24" } ] }
            ] }
            """;

        var llm = new FakeLlmClient(system =>
            system == PromptTemplates.PlannerSystem ? StrategyJson
            : system == PromptTemplates.ProductExtractionSystem ? productsJson
            : "summary");

        var agent = new AgentService(llm,
            new AgentOptions { DefaultGeo = "jordan", Clock = () => FixedNow }, search: search);

        var answer = await agent.AskAsync("ACs in Jordan", "jordan");
        var products = answer.Products!;

        // Brand card now lists the actual models and a price range spanning the brand's offers.
        var samsung = products.Brands.First(b => b.Name == "Samsung");
        samsung.Models.Should().Contain(new[] { "AR18TXHQ", "AR24TXHQ" });
        samsung.PriceFrom.Should().Be(new Money(320, "JOD"));
        samsung.PriceTo.Should().Be(new Money(480, "JOD"));
        samsung.PriceRange.Should().NotBeNullOrEmpty();

        // The extracted verdict rides along on the aggregated model.
        var windFree = products.Models.First(m => m.Model == "AR18TXHQ");
        windFree.Pros.Should().Contain("very quiet");
        windFree.Cons.Should().Contain("pricey filters");
        windFree.ReviewSummary.Should().Contain("efficient pick");
    }

    [Fact]
    public async Task AskAsync_ProductQuery_LlmExtraction_DropsNonLocalOffers()
    {
        var search = new FakeSearchProvider(
            new SearchResult { Title = "guide", Snippet = "options", Url = "https://blog.x/acs", Kind = SearchKind.Web });

        const string productsJson = """
            { "products": [
              { "name": "Gree AC", "brand": "Gree",
                "offers": [
                  { "source": "LocalShop", "price": 300, "currency": "JOD", "url": "https://shop.jo/gree" },
                  { "source": "GlobalShop", "price": 250, "currency": "USD", "url": "https://global-store.com/gree" }
                ] }
            ] }
            """;

        var llm = new FakeLlmClient(system =>
            system == PromptTemplates.PlannerSystem ? StrategyJson
            : system == PromptTemplates.ProductExtractionSystem ? productsJson
            : "summary");

        var agent = new AgentService(llm,
            new AgentOptions { DefaultGeo = "jordan", Clock = () => FixedNow }, search: search);

        var answer = await agent.AskAsync("ACs in Jordan", "jordan");
        var offers = answer.Products!.Models.SelectMany(m => m.Offers).ToList();

        offers.Should().Contain(o => (o.Url ?? "").Contains("shop.jo"));
        offers.Should().NotContain(o => (o.Url ?? "").Contains("global-store.com"));
    }

    [Fact]
    public async Task AskAsync_ProductQuery_LlmExtraction_RetriesOnceThenRecovers()
    {
        // Models sometimes answer the first extraction call with prose instead of JSON. The pass
        // must retry exactly once and parse the recovered response, rather than giving up after
        // a single unparseable reply and leaving the grid empty.
        var search = new FakeSearchProvider(
            new SearchResult { Title = "AC buying guide", Snippet = "options", Url = "https://blog.x/acs", Kind = SearchKind.Web });

        const string productsJson = """
            { "products": [
              { "name": "Samsung WindFree", "brand": "Samsung", "model": "AR18TXHQ",
                "offers": [ { "source": "Smart Buy", "price": 320, "currency": "JOD", "url": "https://shop.jo/ar18" } ] }
            ] }
            """;

        var extractionCalls = 0;
        var llm = new FakeLlmClient(system =>
        {
            if (system == PromptTemplates.PlannerSystem) return StrategyJson;
            if (system == PromptTemplates.ProductExtractionSystem)
            {
                // First reply is unparseable prose; the retry returns valid JSON.
                return ++extractionCalls == 1 ? "Sure! Here are the products you asked for:" : productsJson;
            }
            return "summary";
        });

        var agent = new AgentService(llm,
            new AgentOptions { DefaultGeo = "jordan", Clock = () => FixedNow }, search: search);

        var answer = await agent.AskAsync("ACs in Jordan", "jordan");

        extractionCalls.Should().Be(2); // failed once, retried once
        answer.Products!.Models.Should().Contain(m => m.Brand == "Samsung" && m.Offers.Any(o => o.Price == 320));
    }

    [Fact]
    public async Task AskAsync_ProductQuery_LlmExtraction_FallsBackGracefullyWhenJsonAlwaysBad()
    {
        // When every extraction reply is unparseable, the pass retries once (two attempts total)
        // and then falls back to the deterministic listings instead of throwing. Here the only
        // deterministic source is a shopping hit, which must still surface.
        var search = new FakeSearchProvider(
            new SearchResult
            {
                Title = "Samsung Split AC AR24", Price = new Money(450, "JOD"), Seller = "OpenSooq",
                Url = "https://jo.opensooq.com/en/listing/1", Kind = SearchKind.Shopping
            });

        var llm = new FakeLlmClient(system =>
            system == PromptTemplates.PlannerSystem ? StrategyJson
            : system == PromptTemplates.ProductExtractionSystem ? "not json — just talking"
            : "summary");

        var agent = new AgentService(llm,
            new AgentOptions { DefaultGeo = "jordan", Clock = () => FixedNow }, search: search);

        var answer = await agent.AskAsync("ACs in Jordan", "jordan");

        // Retried exactly once (2 attempts) before falling back.
        llm.SystemPromptsSeen.Count(s => s == PromptTemplates.ProductExtractionSystem).Should().Be(2);
        // The deterministic shopping listing is unaffected by the failed LLM pass.
        answer.Products!.Models.Should().Contain(m => m.Offers.Any(o => o.Price == 450 && o.Currency == "JOD"));
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

    [Fact]
    public async Task ReadPageAsync_NoScraper_ReturnsNull()
    {
        var agent = new AgentService(PlannerAndAnalyst());
        (await agent.ReadPageAsync("https://x.com")).Should().BeNull();
    }

    [Fact]
    public async Task ReadPageAsync_ScrapesThroughConfiguredProvider()
    {
        var agent = new AgentService(PlannerAndAnalyst(), scraper: new FakeScraper("# Specs\nCooling: 24000 BTU"));

        var page = await agent.ReadPageAsync("https://store.jo/ac");

        page.Should().NotBeNull();
        page!.Content.Should().Contain("24000 BTU");
    }

    [Fact]
    public async Task ReadPageAsync_FailedOrEmptyScrape_ReturnsNull()
    {
        var agent = new AgentService(PlannerAndAnalyst(), scraper: new FakeScraper("", success: false));
        (await agent.ReadPageAsync("https://store.jo/ac")).Should().BeNull();
    }
}

/// <summary>Minimal scraper that returns a fixed page, for exercising AgentService.ReadPageAsync.</summary>
file sealed class FakeScraper : IScrapeProvider
{
    private readonly string _content;
    private readonly bool _success;

    public FakeScraper(string content, bool success = true)
    {
        _content = content;
        _success = success;
    }

    public string Name => "fake-scraper";

    public Task<ScrapedPage> ScrapeAsync(
        string url, ScrapeFormat format = ScrapeFormat.Markdown, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ScrapedPage { Url = url, Content = _content, Success = _success });
}
