using System.Text.Json;
using Daleel.Agent;
using Daleel.Core.Geo;
using Daleel.Core.Llm;
using Daleel.Core.Models;
using Daleel.Core.Observability;
using Daleel.Search.Abstractions;
using Daleel.Search.Providers;
using Daleel.Web.Cloudflare;
using Daleel.Web.Pipeline.Enrichment.Actor;
using Daleel.Web.Profiles;
using Daleel.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Daleel.Web.Tests.Profiles;

// ─────────────────────────────────────────────────────────────────────────────────────────────
// The researcher's provider calls must only ever target a REAL site: Google Places' website,
// the caller's saved URL, or an actor-verified discovery (stores only — brand discovery belongs
// to the enrichment queue's BrandResearchHandler). A hostname is NEVER fabricated from the
// entity's display name — a guessed domain that happens to resolve scrapes some unrelated
// company's site and silently attributes it to a local store, which is worse than no data.
// These tests pin that contract by counting every paid call the fakes receive.
// ─────────────────────────────────────────────────────────────────────────────────────────────
public class ContextDevProfileResearcherTests
{
    // example.com / example.org are IANA-reserved and guaranteed to resolve — the researcher's
    // free pre-flight DNS check (SsrfGuard) runs for real in these tests. ".invalid" is
    // RFC-reserved to never resolve, exercising the rejection path just as deterministically.

    [Theory]
    [InlineData("كارفور ماركت")] // Arabic-only name: the old heuristic invented "كارفورماركت.com"
    [InlineData("Leaders Center")] // Latin name: the old heuristic invented "leaderscenter.com"
    public async Task Store_with_no_real_site_source_spends_nothing(string storeName)
    {
        var providers = new RecordingProviderApi(); // HasPlaces=false, no hint, no actor wired
        var researcher = NewResearcher(providers);

        var store = await researcher.ResearchStoreAsync(storeName, "jordan");

        store.Should().NotBeNull("the synthesizer still runs from LLM knowledge");
        providers.ScrapedUrls.Should().BeEmpty("no verified site exists, so nothing may be scraped");
        providers.BrandLookups.Should().BeEmpty();
    }

    [Theory]
    [InlineData("كارفور ماركت")]
    [InlineData("Leaders Center")]
    public async Task Brand_with_no_real_site_source_spends_nothing(string brandName)
    {
        var providers = new RecordingProviderApi();
        var researcher = NewResearcher(providers);

        var brand = await researcher.ResearchBrandAsync(brandName, "jordan");

        brand.Should().NotBeNull();
        providers.ScrapedUrls.Should().BeEmpty();
        providers.BrandLookups.Should().BeEmpty("Context.dev must only ever see a verified domain");
    }

    [Fact]
    public async Task Places_website_becomes_the_stores_scrape_target()
    {
        var providers = new RecordingProviderApi
        {
            Places = new StoreLocation { PlaceId = "p1", Name = "كارفور ماركت", Website = "https://example.com" }
        };
        var researcher = NewResearcher(providers);

        var store = await researcher.ResearchStoreAsync("كارفور ماركت", "jordan");

        providers.ScrapedUrls.Should().ContainSingle().Which.Should().Be("https://example.com");
        store!.GooglePlaceId.Should().Be("p1", "the same Places lookup still verifies the profile");
    }

    [Fact]
    public async Task Places_outranks_the_callers_saved_hint()
    {
        // Legacy rows can carry sites the retired name→hostname heuristic invented; Google's
        // listing is the self-healing correction, so it wins over the saved value.
        var providers = new RecordingProviderApi
        {
            Places = new StoreLocation { PlaceId = "p1", Name = "Smart Buy", Website = "https://example.org" }
        };
        var researcher = NewResearcher(providers);

        await researcher.ResearchStoreAsync("Smart Buy", "jordan", siteUrlHint: "https://example.com/store");

        providers.ScrapedUrls.Should().ContainSingle().Which.Should().Be("https://example.org");
    }

    [Fact]
    public async Task Saved_hint_is_used_when_places_knows_nothing()
    {
        var providers = new RecordingProviderApi(); // no Places
        var researcher = NewResearcher(providers);

        await researcher.ResearchStoreAsync("Smart Buy", "jordan", siteUrlHint: "https://example.com/store");

        providers.ScrapedUrls.Should().ContainSingle().Which.Should().Be("https://example.com/store");
    }

    [Fact]
    public async Task Brand_site_hint_feeds_context_dev_and_the_scrape()
    {
        var providers = new RecordingProviderApi();
        var researcher = NewResearcher(providers);

        var brand = await researcher.ResearchBrandAsync("Acme", "jordan", siteUrlHint: "https://www.example.com");

        providers.BrandLookups.Should().ContainSingle().Which.Should().Be("example.com");
        providers.ScrapedUrls.Should().ContainSingle().Which.Should().Be("https://www.example.com");
        brand!.Website.Should().Be("https://www.example.com", "the researched site persists with the profile");
    }

    [Fact]
    public async Task A_bare_host_hint_is_normalized_before_use()
    {
        var providers = new RecordingProviderApi();
        var researcher = NewResearcher(providers);

        await researcher.ResearchBrandAsync("Acme", "jordan", siteUrlHint: "example.com");

        providers.ScrapedUrls.Should().ContainSingle().Which.Should().Be("https://example.com");
    }

    [Fact]
    public async Task An_unresolvable_hint_is_dropped_before_any_paid_call()
    {
        var providers = new RecordingProviderApi();
        var researcher = NewResearcher(providers);

        await researcher.ResearchBrandAsync("Acme", "jordan", siteUrlHint: "https://acme.invalid");

        providers.ScrapedUrls.Should().BeEmpty();
        providers.BrandLookups.Should().BeEmpty();
    }

    // ── Store-site discovery (the actor workflow) ───────────────────────────────────────────────

    [Fact]
    public async Task Actor_discovery_supplies_the_store_site_when_places_and_hint_have_nothing()
    {
        var providers = new RecordingProviderApi();
        var loop = new ScriptedLoop("{\"website\":\"https://example.com\"}");
        var researcher = NewResearcher(providers, loop);

        using var metered = AmbientApiObserver.Begin(new NullObserver(), new CostEstimator());
        var store = await researcher.ResearchStoreAsync("Some Web Shop", "jordan");

        loop.Runs.Should().Be(1);
        providers.ScrapedUrls.Should().ContainSingle().Which.Should().Be("https://example.com");
        store!.Website.Should().Be("https://example.com");
    }

    [Fact]
    public async Task Actor_discovery_is_skipped_outside_a_metered_flow()
    {
        // A paid multi-turn loop that shows up in no cost ledger is not acceptable: admin/refresh
        // paths without an ambient observer get Places + hints only.
        var providers = new RecordingProviderApi();
        var loop = new ScriptedLoop("{\"website\":\"https://example.com\"}");
        var researcher = NewResearcher(providers, loop);

        await researcher.ResearchStoreAsync("Some Web Shop", "jordan");

        loop.Runs.Should().Be(0);
        providers.ScrapedUrls.Should().BeEmpty();
    }

    [Fact]
    public async Task Actor_output_must_still_pass_the_preflight()
    {
        // The loop's answer is LLM text from untrusted pages — a hallucinated or dead host must be
        // rejected by the same free DNS check as every other candidate, before any paid call.
        var providers = new RecordingProviderApi();
        var loop = new ScriptedLoop("{\"website\":\"https://phantom.invalid\"}");
        var researcher = NewResearcher(providers, loop);

        using var metered = AmbientApiObserver.Begin(new NullObserver(), new CostEstimator());
        await researcher.ResearchStoreAsync("Some Web Shop", "jordan");

        loop.Runs.Should().Be(1);
        providers.ScrapedUrls.Should().BeEmpty();
    }

    private static ContextDevProfileResearcher NewResearcher(
        RecordingProviderApi providers, IActorLoop? loop = null) =>
        new(new StubAgentFactory(), NullLogger<ContextDevProfileResearcher>.Instance, providers,
            loop is null ? null : new StoreSiteActor(loop));

    /// <summary>Factory double: a canned LLM for the synthesizer and a bare agent for the actor.</summary>
    private sealed class StubAgentFactory : IAgentFactory
    {
        public bool HasLlm() => true;
        public ProviderStatus Describe() => new(true, false, false, true, false);
        public AgentService Build(AgentRequest request) => new(new CannedLlm());
        public ILlmClient? TryBuildLlm(string? model = null) => new CannedLlm();
        public string? Resolve(string name) => null;
    }

    private sealed class CannedLlm : ILlmClient
    {
        public string Provider => "canned";
        public Task<LlmResponse> CompleteAsync(
            string systemPrompt, IReadOnlyList<LlmMessage> messages, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LlmResponse { Content = "{}" });
    }

    private sealed class NullObserver : IApiCallObserver
    {
        public void Record(ApiCall call) { }
    }

    /// <summary>An actor loop that returns a fixed done-result without touching the agent.</summary>
    private sealed class ScriptedLoop : IActorLoop
    {
        private readonly string _resultJson;
        public int Runs { get; private set; }

        public ScriptedLoop(string resultJson) => _resultJson = resultJson;

        public Task<ActorResult> RunAsync(
            AgentService agent, string guidingSystem, string initialContext,
            IReadOnlyList<ActorTool> tools, ActorToolDispatch dispatch, ActorBounds bounds, CancellationToken ct)
        {
            Runs++;
            var result = JsonSerializer.Deserialize<JsonElement>(_resultJson);
            return Task.FromResult(new ActorResult(true, result, Array.Empty<string>()));
        }
    }

    /// <summary>Records every paid call so the tests can assert exactly what was (not) spent.</summary>
    private sealed class RecordingProviderApi : IProviderApi
    {
        public List<string> ScrapedUrls { get; } = new();
        public List<string> BrandLookups { get; } = new();
        public StoreLocation? Places { get; init; }

        public bool HasScraper => true;
        public bool HasPlaces => Places is not null;
        public bool HasEdge => false;
        public bool EdgeDrainReady => false;
        public bool HasSocial => false;
        public bool HasEdgeExtract => false;
        public bool HasEdgeClassify => false;
        public bool HasEdgeFilter => false;

        public Task<ScrapedPage?> ScrapePageAsync(
            string url, ScrapeFormat format = ScrapeFormat.Markdown, CancellationToken ct = default)
        {
            ScrapedUrls.Add(url);
            return Task.FromResult<ScrapedPage?>(new ScrapedPage { Content = "About us.", Success = true });
        }

        public Task<BrandProfile?> GetBrandAsync(string domain, CancellationToken ct = default)
        {
            BrandLookups.Add(domain);
            return Task.FromResult<BrandProfile?>(null);
        }

        public Task<IReadOnlyList<StoreLocation>> SearchPlacesAsync(
            string query, GeoPoint? near = null, double radiusMeters = 5000, string? languageCode = null,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<StoreLocation>>(
                Places is null ? Array.Empty<StoreLocation>() : new[] { Places });

        public Task<StoreLocation?> GetPlaceDetailsAsync(string placeId, CancellationToken ct = default) =>
            Task.FromResult<StoreLocation?>(null); // researcher falls back to the search match

        public Task<IReadOnlyList<CatalogProduct>> ExtractCatalogAsync(
            string domain, int maxProducts = 0, int timeoutMs = 45_000, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CatalogProduct>>(Array.Empty<CatalogProduct>());
        public Task<IReadOnlyList<SocialPost>> FetchSocialPostsAsync(
            Source source, string? keyword = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SocialPost>>(Array.Empty<SocialPost>());
        public Task<WorkerHandle?> SubmitEdgeCatalogAsync(
            string domain, string? store, string? searchJobId, int maxProducts = 0, CancellationToken ct = default) =>
            Task.FromResult<WorkerHandle?>(null);
        public Task<WorkerHandle?> SubmitEdgeBrandAsync(
            string domain, string brandName, string? searchJobId, bool refresh = false, CancellationToken ct = default) =>
            Task.FromResult<WorkerHandle?>(null);
        public Task<IReadOnlyList<ClassifyVerdict>> ClassifyTextAsync(
            IReadOnlyList<(string Id, string Text)> items, IReadOnlyList<string> labels, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ClassifyVerdict>>(Array.Empty<ClassifyVerdict>());
        public Task<IReadOnlyList<CatalogProduct>> ExtractProductsFromContentAsync(
            string content, string? market = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CatalogProduct>>(Array.Empty<CatalogProduct>());
        public Task<IReadOnlyList<FilterFindingDto>> FilterTextFindingsAsync(
            IReadOnlyList<(string Id, string Text, string? SourceUrl)> items, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<FilterFindingDto>>(Array.Empty<FilterFindingDto>());
        public Task<IReadOnlyList<FilterFindingDto>> FilterImageFindingsAsync(
            IReadOnlyList<string> urls, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<FilterFindingDto>>(Array.Empty<FilterFindingDto>());
    }
}
