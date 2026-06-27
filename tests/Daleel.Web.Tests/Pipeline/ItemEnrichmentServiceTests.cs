using Daleel.Agent;
using Daleel.Core.Intelligence;
using Daleel.Core.Llm;
using Daleel.Core.Models;
using Daleel.Search.Abstractions;
using Daleel.Web.Data;
using Daleel.Web.Pipeline;
using Daleel.Web.Profiles;
using Daleel.Web.Services;
using Daleel.Web.Storage;
using Daleel.Web.Tests.Data;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Daleel.Web.Tests.Pipeline;

/// <summary>
/// The core deep-dive orchestration (<see cref="ItemEnrichmentService.EnrichAsync"/>) had no tests — only
/// the static URL-picker helper did. These cover the two branches that carry the correctness: DB-first
/// reuse of a fresh saved profile (no network), and the scrape→persist path that saves a fresh deep-dive
/// so the next search reuses it. Everything network-bound is faked, so these run offline.
/// </summary>
public class ItemEnrichmentServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 27, 12, 0, 0, TimeSpan.Zero);

    private static ItemEnrichmentService Build(SqliteTestContext ctx, IBrandCatalogService? catalog = null) =>
        new(
            new ProductProfileRepository(ctx.Db),
            new ProfileOptions { Now = () => Now, Ttl = TimeSpan.FromDays(30) },
            new StubAgentFactory(),
            new ScrapedPriceRepository(ctx.Db),
            new NullR2StorageService(),
            catalog ?? new StubBrandCatalog(),
            NullLogger<ItemEnrichmentService>.Instance);

    [Fact]
    public async Task EnrichAsync_NoModels_ReturnsNoChange()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);

        var result = await svc.EnrichAsync(NoScrapeAgent(), new ProductSearchResult(), _ => { }, "sid", default);

        result.Products.Should().BeNull("nothing to enrich");
    }

    [Fact]
    public async Task EnrichAsync_ReusesFreshSavedProfile_WithoutScraping()
    {
        using var ctx = new SqliteTestContext();
        var key = ProductProfile.KeyFor("Samsung", "S24", "Galaxy S24");
        await new ProductProfileRepository(ctx.Db).UpsertAsync(new ProductProfile
        {
            Name = "Galaxy S24", Brand = "Samsung", Model = "S24", NameKey = key,
            Details = "CACHED SPECS", SourceUrl = "https://samsung.com/jo/s24", LastRefreshed = Now
        });

        var svc = Build(ctx);
        // A scraper that throws proves the reuse path never reaches the network.
        var agent = new AgentService(new StubLlm(), new AgentOptions(), scraper: new ThrowingScraper());
        var model = new ProductModel
        {
            Name = "Galaxy S24", Brand = "Samsung", Model = "S24",
            Specs = new Dictionary<string, string> { ["a"] = "1", ["b"] = "2", ["c"] = "3" }, // not thin
            Offers = new[] { new PriceOffer { Source = "SmartBuy", Url = "https://store.jo/s24", Price = 900m } }
        };
        var products = new ProductSearchResult { Models = new[] { model } };

        var result = await svc.EnrichAsync(agent, products, _ => { }, "sid", default);

        result.Products.Should().NotBeNull();
        result.Products!.Models.Single().Specs.Should().ContainKey("details")
            .WhoseValue.Should().Be("CACHED SPECS", "the fresh saved profile is reused into the model's specs");
    }

    [Fact]
    public async Task EnrichAsync_ScrapesAndPersists_AThinUncachedModel()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);
        var agent = new AgentService(new StubLlm(), new AgentOptions(),
            scraper: new FixedScraper("# Specs\nCooling: 24000 BTU"));

        var model = new ProductModel
        {
            Name = "LG DualCool", Brand = "LG", Model = "DualCool",
            Specs = new Dictionary<string, string>(), // thin → eligible for a scrape
            Offers = new[] { new PriceOffer { Source = "Store", Url = "https://store.jo/lg", Price = 500m, IsLowest = true, SourceType = ResultType.StorePage } }
        };
        var products = new ProductSearchResult { Models = new[] { model } };

        var result = await svc.EnrichAsync(agent, products, _ => { }, "sid", default);

        result.Products!.Models.Single().Specs["details"].Should().Contain("24000 BTU");

        // The deep-dive must be persisted so a later search reuses it instead of re-scraping.
        var key = ProductProfile.KeyFor("LG", "DualCool", "LG DualCool");
        var saved = await new ProductProfileRepository(ctx.NewContext()).GetByKeyAsync(key);
        saved.Should().NotBeNull();
        saved!.Details.Should().Contain("24000 BTU");
    }

    private static AgentService NoScrapeAgent() =>
        new(new StubLlm(), new AgentOptions(), scraper: new ThrowingScraper());

    // ── Fakes ─────────────────────────────────────────────────────────────────────

    private sealed class StubLlm : ILlmClient
    {
        public string Provider => "stub";
        public Task<LlmResponse> CompleteAsync(
            string systemPrompt, IReadOnlyList<LlmMessage> messages, CancellationToken ct = default) =>
            Task.FromResult(new LlmResponse { Content = "{}" });
    }

    private sealed class FixedScraper : IScrapeProvider
    {
        private readonly string _content;
        public FixedScraper(string content) => _content = content;
        public string Name => "fixed-scraper";
        public Task<ScrapedPage> ScrapeAsync(string url, ScrapeFormat format = ScrapeFormat.Markdown, CancellationToken ct = default) =>
            Task.FromResult(new ScrapedPage { Url = url, Content = _content, Success = true });
    }

    private sealed class ThrowingScraper : IScrapeProvider
    {
        public string Name => "throwing-scraper";
        public Task<ScrapedPage> ScrapeAsync(string url, ScrapeFormat format = ScrapeFormat.Markdown, CancellationToken ct = default) =>
            throw new InvalidOperationException("scraper must not be called on the reuse path");
    }

    // Resolve returns null so the Context.dev catalogue/harvest phases no-op without a key.
    private sealed class StubAgentFactory : IAgentFactory
    {
        public bool HasLlm(IReadOnlyDictionary<string, string>? keys = null) => false;
        public ProviderStatus Describe(IReadOnlyDictionary<string, string>? keys = null) => throw new NotSupportedException();
        public AgentService Build(AgentRequest request) => throw new NotSupportedException();
        public ILlmClient? TryBuildLlm(string? model = null, IReadOnlyDictionary<string, string>? keys = null) => null;
        public string? Resolve(string name, IReadOnlyDictionary<string, string>? keys = null) => null;
    }

    private sealed class StubBrandCatalog : IBrandCatalogService
    {
        public Task<int> HarvestAsync(string brandName, CancellationToken ct = default) => Task.FromResult(0);
    }
}
