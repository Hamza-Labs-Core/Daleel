using Daleel.Agent;
using Daleel.Core.Geo;
using Daleel.Core.Llm;
using Daleel.Core.Models;
using Daleel.Search.Abstractions;
using Daleel.Search.Providers;
using Daleel.Web.Cloudflare;
using Daleel.Web.Data;
using Daleel.Web.Profiles;
using Daleel.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Daleel.Web.Tests.Pipeline;

// ─────────────────────────────────────────────────────────────────────────────────────────────
// When the paid catalogue provider is down (Context.dev out of credits ⇒ 401), the brand harvest
// must fall through to the Cloudflare-Browser render + LLM extraction — the worker-independent
// fallback that keeps working when the paid providers don't.
//
// The regression pinned here: the GLOBAL, name-based harvest (HarvestAsync(brand)) passes
// countryCode = null, and the old guard `GeoProfiles.Resolve(null)` is null — so the pattern
// `Resolve(countryCode) is { } market` was false and the ENTIRE fallback was silently skipped.
// A 401 then left the brand with zero models, indistinguishable from "this brand had none".
// The guard now uses ResolveOrDefault (null country ⇒ no local targeting ⇒ default market), so the
// fallback fires regardless of country. We prove it fires by observing the render call it makes.
// ─────────────────────────────────────────────────────────────────────────────────────────────
public class BrandCatalogFallbackTests
{
    [Fact]
    public async Task Global_harvest_renders_the_site_when_the_paid_catalogue_provider_401s()
    {
        // A public-IP website keeps BOTH SsrfGuard checks hermetic: an IP literal is validated as
        // public with no DNS round-trip, so the test never touches the network.
        var brand = new Brand { Id = 7, Name = "Sharp", Website = "http://93.184.216.34" };
        var scraper = new RecordingScraper("# Sharp\nAQUOS 55\" 4K TV");
        var service = new BrandCatalogService(
            new FakeBrandRepo(brand),
            new RecordingModelRepo(),
            new FakeAgentFactory(scraper),
            new ProfileOptions(),
            NullLogger<BrandCatalogService>.Instance,
            new FaultyCatalogProviderApi());

        // Global path: no country — this is the exact call the old guard broke.
        await service.HarvestAsync("Sharp");

        scraper.Rendered.Should().BeTrue(
            "a Context.dev 401 on the country-less global path must fall through to the " +
            "Cloudflare-Browser render + LLM extraction, not give up");
    }

    // ── Fakes ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Records whether the agent ever asked it to render a page (the fallback's tell).</summary>
    private sealed class RecordingScraper : IScrapeProvider
    {
        private readonly string _content;
        public RecordingScraper(string content) => _content = content;
        public bool Rendered { get; private set; }
        public string Name => "recording-scraper";

        public Task<ScrapedPage> ScrapeAsync(
            string url, ScrapeFormat format = ScrapeFormat.Markdown, CancellationToken ct = default)
        {
            Rendered = true;
            return Task.FromResult(new ScrapedPage { Url = url, Content = _content, Success = true });
        }
    }

    /// <summary>Stub LLM: names no products, so the harvest yields nothing — the test only asserts
    /// the fallback path was ENTERED (the render), not what the extractor returned.</summary>
    private sealed class StubLlm : ILlmClient
    {
        public string Provider => "stub";
        public Task<LlmResponse> CompleteAsync(
            string systemPrompt, IReadOnlyList<LlmMessage> messages, CancellationToken ct = default) =>
            Task.FromResult(new LlmResponse { Content = "{}" });
    }

    /// <summary>Builds a real AgentService around the recording scraper — HasLlm true so the fallback
    /// guard passes, exactly as production does when an LLM key is configured.</summary>
    private sealed class FakeAgentFactory : IAgentFactory
    {
        private readonly IScrapeProvider _scraper;
        public FakeAgentFactory(IScrapeProvider scraper) => _scraper = scraper;

        public bool HasLlm() => true;
        public ProviderStatus Describe() => throw new NotSupportedException();
        public AgentService Build(AgentRequest request) =>
            new(new StubLlm(), new AgentOptions(), scraper: _scraper);
        public ILlmClient? TryBuildLlm(string? model = null) => new StubLlm();
        public string? Resolve(string name) => null;
    }

    private sealed class FakeBrandRepo : IBrandRepository
    {
        private readonly Brand _row;
        public FakeBrandRepo(Brand row) => _row = row;
        public Task<Brand?> GetByNameAsync(string name, CancellationToken ct = default) =>
            Task.FromResult<Brand?>(string.Equals(name, _row.Name, StringComparison.OrdinalIgnoreCase) ? _row : null);
        public Task<Brand?> GetByIdAsync(int id, CancellationToken ct = default) =>
            Task.FromResult<Brand?>(id == _row.Id ? _row : null);
        public Task<Brand> UpsertAsync(Brand brand, CancellationToken ct = default) => Task.FromResult(brand);
        public Task<IReadOnlyList<Brand>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Brand>>(new[] { _row });
        public Task<IReadOnlyList<Brand>> ListStaleAsync(DateTimeOffset olderThan, int max, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Brand>>(Array.Empty<Brand>());
        public Task<int> CountAsync(CancellationToken ct = default) => Task.FromResult(1);

        public Task<IReadOnlyList<Brand>> SearchAsync(string? query, int skip, int take, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Brand>>(Array.Empty<Brand>());
    }

    private sealed class RecordingModelRepo : IBrandModelRepository
    {
        public List<BrandModel> Upserted { get; } = new();
        public Task<BrandModel> UpsertAsync(BrandModel model, CancellationToken ct = default)
        {
            Upserted.Add(model);
            return Task.FromResult(model);
        }

        // Empty ⇒ the per-(brand, level) freshness gate always lets the harvest run.
        public Task<IReadOnlyList<BrandModel>> ListByBrandAsync(int brandId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<BrandModel>>(Array.Empty<BrandModel>());
        public Task<BrandModel?> GetByBrandAndKeyAsync(int brandId, string modelKey, CancellationToken ct = default) =>
            Task.FromResult<BrandModel?>(null);
        public Task<BrandModel?> GetByIdAsync(int id, CancellationToken ct = default) =>
            Task.FromResult<BrandModel?>(null);
        public Task SaveFinalSpecsAsync(int id, string? finalSpecsJson, string? finalSpecsR2Url, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task<BrandModel?> FindByProductKeyAsync(string productKey, CancellationToken ct = default) =>
            Task.FromResult<BrandModel?>(null);
        public Task<int> CountForBrandAsync(int brandId, CancellationToken ct = default) =>
            Task.FromResult(Upserted.Count);
        public Task<int> CountAsync(CancellationToken ct = default) => Task.FromResult(Upserted.Count);
    }

    /// <summary>Context.dev configured (HasScraper) but its catalogue extract throws — the 401 shape
    /// the pipeline hits when the key is out of credits.</summary>
    private sealed class FaultyCatalogProviderApi : IProviderApi
    {
        public bool HasScraper => true;
        public bool HasEdge => false;
        public bool EdgeDrainReady => false;
        public bool HasPlaces => false;
        public bool HasSocial => false;
        public bool HasEdgeExtract => false;
        public bool HasEdgeClassify => false;
        public bool HasEdgeFilter => false;

        public Task<IReadOnlyList<CatalogProduct>> ExtractCatalogAsync(
            string domain, int maxProducts = 0, int timeoutMs = 45_000, CancellationToken ct = default) =>
            throw new HttpRequestException("401 Unauthorized (out of credits)");

        public Task<BrandProfile?> GetBrandAsync(string domain, CancellationToken ct = default) =>
            Task.FromResult<BrandProfile?>(null);
        public Task<ScrapedPage?> ScrapePageAsync(string url, ScrapeFormat format = ScrapeFormat.Markdown, CancellationToken ct = default) =>
            Task.FromResult<ScrapedPage?>(null);
        public Task<IReadOnlyList<StoreLocation>> SearchPlacesAsync(string query, GeoPoint? near = null, double radiusMeters = 5000, string? languageCode = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<StoreLocation>>(Array.Empty<StoreLocation>());
        public Task<StoreLocation?> GetPlaceDetailsAsync(string placeId, CancellationToken ct = default) =>
            Task.FromResult<StoreLocation?>(null);
        public Task<IReadOnlyList<SocialPost>> FetchSocialPostsAsync(Source source, string? keyword = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SocialPost>>(Array.Empty<SocialPost>());
        public Task<WorkerHandle?> SubmitEdgeCatalogAsync(string domain, string? store, string? searchJobId, int maxProducts = 0, CancellationToken ct = default) =>
            Task.FromResult<WorkerHandle?>(null);
        public Task<WorkerHandle?> SubmitEdgeBrandAsync(string domain, string brandName, string? searchJobId, bool refresh = false, CancellationToken ct = default) =>
            Task.FromResult<WorkerHandle?>(null);
        public Task<IReadOnlyList<ClassifyVerdict>> ClassifyTextAsync(IReadOnlyList<(string Id, string Text)> items, IReadOnlyList<string> labels, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ClassifyVerdict>>(Array.Empty<ClassifyVerdict>());
        public Task<IReadOnlyList<CatalogProduct>> ExtractProductsFromContentAsync(string content, string? market = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CatalogProduct>>(Array.Empty<CatalogProduct>());
        public Task<IReadOnlyList<FilterFindingDto>> FilterTextFindingsAsync(IReadOnlyList<(string Id, string Text, string? SourceUrl)> items, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<FilterFindingDto>>(Array.Empty<FilterFindingDto>());
        public Task<IReadOnlyList<FilterFindingDto>> FilterImageFindingsAsync(IReadOnlyList<string> urls, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<FilterFindingDto>>(Array.Empty<FilterFindingDto>());
    }
}
