using Daleel.Core.Llm;
using Daleel.Web.Data;
using Daleel.Web.Profiles;
using Daleel.Web.Tests.Data;
using FluentAssertions;
using Xunit;

namespace Daleel.Web.Tests.Profiles;

/// <summary>Minimal deterministic LLM that returns a fixed completion regardless of the prompt.</summary>
file sealed class FakeLlm : ILlmClient
{
    private readonly string _response;
    public FakeLlm(string response) => _response = response;
    public string Provider => "fake";
    public Task<LlmResponse> CompleteAsync(
        string systemPrompt, IReadOnlyList<LlmMessage> messages, CancellationToken ct = default) =>
        Task.FromResult(new LlmResponse { Content = _response });
}

/// <summary>
/// The DB-first / staleness contract that keeps Context.dev research cheap: a profile is only
/// (re)researched when it is missing or older than the TTL, and a search that already has a fresh
/// saved profile never calls out. Also covers the LLM-JSON → entity synthesis and the graceful
/// degradation when no research keys are configured.
/// </summary>
public class ProfileServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 25, 12, 0, 0, TimeSpan.Zero);

    private static ProfileOptions Options(DateTimeOffset? now = null) => new()
    {
        Ttl = TimeSpan.FromDays(30),
        Now = () => now ?? Now
    };

    // ── Synthesis ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Synthesizer_BuildsBrandFromLlmJson()
    {
        var llm = new FakeLlm("""
        {
          "countryOfOrigin": "South Korea",
          "reputationScore": 8.4,
          "description": "Global electronics maker.",
          "pros": ["reliable", "broad lineup"],
          "cons": ["premium pricing"],
          "popularModels": ["Galaxy S24", "Bespoke fridge"],
          "priceRange": "mid-to-premium",
          "website": "https://samsung.com"
        }
        """);
        var synth = new ProfileSynthesizer(llm);

        var brand = await synth.SynthesizeBrandAsync("Samsung", "Context about Samsung reviews...", default);

        brand.Name.Should().Be("Samsung");
        brand.CountryOfOrigin.Should().Be("South Korea");
        brand.ReputationScore.Should().Be(8.4);
        brand.Pros.Should().BeEquivalentTo("reliable", "broad lineup");
        brand.PopularModels.Should().Contain("Galaxy S24");
        brand.PriceRange.Should().Be("mid-to-premium");
    }

    [Fact]
    public async Task Synthesizer_BuildsStoreFromLlmJson()
    {
        var llm = new FakeLlm("""
        { "location": "Amman, Jordan", "type": "electronics retailer",
          "brandsCarried": ["Samsung", "LG"], "rating": 4.2, "website": "https://x.com",
          "phone": "+962 6 123 4567", "email": "info@smartbuy.jo", "address": "King St, Amman" }
        """);
        var synth = new ProfileSynthesizer(llm);

        var store = await synth.SynthesizeStoreAsync("Smart Buy", "Context...", default);

        store.Name.Should().Be("Smart Buy");
        store.Location.Should().Be("Amman, Jordan");
        store.BrandsCarried.Should().BeEquivalentTo("Samsung", "LG");
        store.Rating.Should().Be(4.2);
        store.Phone.Should().Be("+962 6 123 4567");
        store.Email.Should().Be("info@smartbuy.jo");
        store.Address.Should().Be("King St, Amman");
    }

    // ── DB-first / staleness ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetOrCreate_ResearchesAndPersists_WhenMissing()
    {
        using var ctx = new PostgresTestContext();
        var repo = new BrandRepository(ctx.Db);
        var researcher = new SpyResearcher(brand: new Brand { Name = "Samsung", Description = "fresh" });
        var svc = new BrandProfileService(repo, researcher, Options());

        var result = await svc.GetOrCreateAsync("Samsung");

        result.Should().NotBeNull();
        result!.Description.Should().Be("fresh");
        result.LastRefreshed.Should().Be(Now, "the service stamps the refresh time");
        (await repo.GetByNameAsync("Samsung")).Should().NotBeNull("it was persisted");
        researcher.BrandCalls.Should().Be(1);
    }

    [Fact]
    public async Task GetOrCreate_ReturnsCached_WithoutResearching_WhenFresh()
    {
        using var ctx = new PostgresTestContext();
        var repo = new BrandRepository(ctx.Db);
        await repo.UpsertAsync(new Brand { Name = "LG", Description = "cached", LastRefreshed = Now.AddDays(-5) });
        var researcher = new SpyResearcher(brand: new Brand { Name = "LG", Description = "should-not-be-used" });
        var svc = new BrandProfileService(repo, researcher, Options());

        var result = await svc.GetOrCreateAsync("LG");

        result!.Description.Should().Be("cached");
        researcher.BrandCalls.Should().Be(0, "a fresh profile must not trigger Context.dev research");
    }

    [Fact]
    public async Task GetOrCreate_Refreshes_WhenStale()
    {
        using var ctx = new PostgresTestContext();
        var repo = new BrandRepository(ctx.Db);
        await repo.UpsertAsync(new Brand { Name = "Sony", Description = "old", LastRefreshed = Now.AddDays(-40) });
        var researcher = new SpyResearcher(brand: new Brand { Name = "Sony", Description = "updated" });
        var svc = new BrandProfileService(repo, researcher, Options());

        var result = await svc.GetOrCreateAsync("Sony");

        result!.Description.Should().Be("updated");
        researcher.BrandCalls.Should().Be(1);
    }

    [Fact]
    public async Task GetOrCreate_FallsBackToStaleProfile_WhenResearchUnavailable()
    {
        using var ctx = new PostgresTestContext();
        var repo = new BrandRepository(ctx.Db);
        await repo.UpsertAsync(new Brand { Name = "Bosch", Description = "stale-but-real", LastRefreshed = Now.AddDays(-99) });
        var researcher = new SpyResearcher(brand: null); // no keys → research returns null
        var svc = new BrandProfileService(repo, researcher, Options());

        var result = await svc.GetOrCreateAsync("Bosch");

        result!.Description.Should().Be("stale-but-real", "a stale profile beats nothing when research is unavailable");
    }

    [Fact]
    public async Task RefreshStale_RefreshesEveryStaleProfile()
    {
        using var ctx = new PostgresTestContext();
        var repo = new BrandRepository(ctx.Db);
        await repo.UpsertAsync(new Brand { Name = "A", LastRefreshed = Now.AddDays(-40) });
        await repo.UpsertAsync(new Brand { Name = "B", LastRefreshed = Now.AddDays(-50) });
        await repo.UpsertAsync(new Brand { Name = "Fresh", LastRefreshed = Now.AddDays(-1) });
        var researcher = new SpyResearcher(brand: new Brand { Name = "_", Description = "r" }, echoName: true);
        var svc = new BrandProfileService(repo, researcher, Options());

        var count = await svc.RefreshStaleAsync(max: 10);

        count.Should().Be(2, "only the two stale brands are refreshed");
        researcher.BrandCalls.Should().Be(2);
    }

    // ── siteUrlHint plumbing ─────────────────────────────────────────────────────
    // The hint IS the "never re-discover a site we already know" optimization: if a service stops
    // passing the saved Website, every stale refresh silently re-pays site discovery.

    [Fact]
    public async Task GetOrCreate_SeedsResearchWithTheSavedWebsite_WhenStale()
    {
        using var ctx = new PostgresTestContext();
        var repo = new BrandRepository(ctx.Db);
        await repo.UpsertAsync(new Brand
        {
            Name = "Sony", Website = "https://sony.example", LastRefreshed = Now.AddDays(-40)
        });
        var researcher = new SpyResearcher(brand: new Brand { Name = "Sony", Description = "updated" });
        var svc = new BrandProfileService(repo, researcher, Options());

        await svc.GetOrCreateAsync("Sony");

        researcher.LastBrandHint.Should().Be("https://sony.example",
            "the stale row's site is a real URL a previous pass established — research must not re-discover it");
    }

    [Fact]
    public async Task Refresh_PassesNoHint_SoASuspectSiteGetsReestablished()
    {
        using var ctx = new PostgresTestContext();
        var repo = new BrandRepository(ctx.Db);
        await repo.UpsertAsync(new Brand
        {
            Name = "Sony", Website = "https://wrong.example", LastRefreshed = Now.AddDays(-1)
        });
        var researcher = new SpyResearcher(brand: new Brand { Name = "Sony", Description = "re-done" });
        var svc = new BrandProfileService(repo, researcher, Options());

        await svc.RefreshAsync("Sony");

        researcher.BrandCalls.Should().Be(1);
        researcher.LastBrandHint.Should().BeNull(
            "a FORCED refresh exists to correct a wrong profile — seeding it with the saved site would re-confirm the mistake");
    }

    /// <summary>Configurable researcher double that counts calls and returns canned entities (or null).</summary>
    private sealed class SpyResearcher : IProfileResearcher
    {
        private readonly Brand? _brand;
        private readonly Store? _store;
        private readonly bool _echoName;
        public int BrandCalls { get; private set; }
        public int StoreCalls { get; private set; }
        public string? LastBrandHint { get; private set; }
        public string? LastStoreHint { get; private set; }
        public bool IsAvailable => true;

        public SpyResearcher(Brand? brand = null, Store? store = null, bool echoName = false)
        {
            _brand = brand;
            _store = store;
            _echoName = echoName;
        }

        public Task<Brand?> ResearchBrandAsync(
            string brandName, string? geo, CancellationToken ct = default, string? siteUrlHint = null)
        {
            BrandCalls++;
            LastBrandHint = siteUrlHint;
            if (_brand is null) return Task.FromResult<Brand?>(null);
            return Task.FromResult<Brand?>(new Brand
            {
                Name = _echoName ? brandName : _brand.Name,
                Description = _brand.Description
            });
        }

        public Task<Store?> ResearchStoreAsync(string storeName, string? geo, CancellationToken ct = default, string? siteUrlHint = null)
        {
            StoreCalls++;
            LastStoreHint = siteUrlHint;
            return Task.FromResult(_store);
        }
    }
}
