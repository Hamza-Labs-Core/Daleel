using System.Collections.Concurrent;
using Daleel.Agent;
using Daleel.Core.Llm;
using Daleel.Core.Models;
using Daleel.Web.Data;
using Daleel.Web.Identification;
using Daleel.Web.Pipeline.SubWorkflows;
using Daleel.Web.Profiles;
using Daleel.Web.Services;
using Daleel.Web.Storage;
using Elsa.Extensions;
using Elsa.Workflows;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Daleel.Web.Tests.Pipeline;

/// <summary>
/// Exercises the per-entity research sub-workflows (brand / store / item) and the parallel dispatcher
/// that fans them out. Each sub-workflow runs through the real Elsa runner against in-memory repos +
/// a fake researcher, so the tests cover the activity sequencing, the DB-first staleness short-circuit,
/// the field-mapping back onto the result, and the nested-runner-in-child-scope fan-out.
/// </summary>
public class SubWorkflowTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // ── Brand ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BrandResearchWorkflow_Researches_Enriches_AndSaves()
    {
        var researcher = new FakeResearcher
        {
            Brand = name => new Brand
            {
                Name = name, NameKey = Brand.Normalize(name), ReputationScore = 8,
                Description = "Reliable", Pros = { "Quiet" }, Cons = { "Pricey" }, Website = "https://samsung.com"
            }
        };
        var brandRepo = new InMemoryBrandRepo();
        using var provider = BuildProvider(s =>
        {
            s.AddSingleton<IProfileResearcher>(researcher);
            s.AddSingleton<IBrandRepository>(brandRepo);
        });

        var state = await RunBrandAsync(provider, new BrandInfo { Name = "Samsung" });

        state.Result.Reputation.Should().NotBeNull();
        state.Result.Reputation!.Score.Should().Be(4.0, "the 0–10 score maps onto the UI's 1–5 scale");
        state.Result.Reputation.Summary.Should().Be("Reliable");
        state.Result.Url.Should().Be("https://samsung.com");
        (await brandRepo.GetByNameAsync("Samsung")).Should().NotBeNull("a fresh research pass is persisted");
        state.Events.Should().Contain(e => e.EventType == "profile.brand");
    }

    [Fact]
    public async Task BrandResearchWorkflow_ReusesFreshSavedProfile_WithoutResearching()
    {
        var brandRepo = new InMemoryBrandRepo();
        await brandRepo.UpsertAsync(new Brand
        {
            Name = "Samsung", NameKey = Brand.Normalize("Samsung"), ReputationScore = 6,
            Description = "Known", Website = "https://samsung.com", LastRefreshed = Now
        });
        var researcher = new FakeResearcher { Brand = _ => throw new InvalidOperationException("must not research") };

        using var provider = BuildProvider(s =>
        {
            s.AddSingleton<IProfileResearcher>(researcher);
            s.AddSingleton<IBrandRepository>(brandRepo);
        });

        var state = await RunBrandAsync(provider, new BrandInfo { Name = "Samsung" });

        researcher.BrandCalls.Should().Be(0, "a fresh saved profile must short-circuit the network research");
        state.Result.Reputation!.Score.Should().Be(3.0);
    }

    [Fact]
    public async Task BrandResearchWorkflow_KeepsSourceLogo_EvenWhenR2Configured()
    {
        // Policy: images are source URLs, never re-hosted in R2. Even with a fully-configured R2 that WOULD
        // host whatever it's handed, the logo must stay the original source URL — the step never uploads it.
        // (Re-hosting depended on the images bucket's public host matching the write target; a mismatch 404'd
        // every "hosted" image, so we hot-link the source instead.)
        using var provider = BuildProvider(s => s.AddSingleton<IR2StorageService>(new FakeImageR2()));

        var state = await RunBrandAsync(
            provider, new BrandInfo { Name = "Samsung", LogoUrl = "https://cdn.samsung.com/logo.png" });

        state.Result.LogoUrl.Should().Be("https://cdn.samsung.com/logo.png",
            "the logo is rendered from its source URL and never copied into R2");
        state.Events.Should().Contain(e => e.EventType == "brand.images" && e.Success);
    }

    [Fact]
    public async Task BrandResearchWorkflow_WithoutR2_KeepsSourceLogo()
    {
        // With no R2 at all the behaviour is identical — the logo URL survives untouched.
        using var provider = BuildProvider(_ => { });

        var state = await RunBrandAsync(
            provider, new BrandInfo { Name = "Samsung", LogoUrl = "https://cdn.samsung.com/logo.png" });

        state.Result.LogoUrl.Should().Be("https://cdn.samsung.com/logo.png");
        state.Events.Should().Contain(e => e.EventType == "brand.images");
    }

    // ── Store ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task StoreResearchWorkflow_Researches_Verifies_AndEnriches()
    {
        var researcher = new FakeResearcher
        {
            Store = name => new Store
            {
                Name = name, NameKey = Store.Normalize(name), GoogleRating = 4.5, GoogleReviewCount = 120,
                GooglePlaceId = "pid-1", Address = "123 King St", Phone = "+962 6 000 0000",
                Latitude = 31.95, Longitude = 35.91, Website = "https://abcstore.com"
            }
        };
        var storeRepo = new InMemoryStoreRepo();
        using var provider = BuildProvider(s =>
        {
            s.AddSingleton<IProfileResearcher>(researcher);
            s.AddSingleton<IStoreRepository>(storeRepo);
            s.AddSingleton<IAgentFactory>(new FakeAgentFactory());
        });

        var state = await RunStoreAsync(provider, new StoreInfo { Name = "ABC Store" });

        state.Result.Rating.Should().Be(4.5);
        state.Result.ReviewCount.Should().Be(120);
        state.Result.Address.Should().Be("123 King St");
        state.Result.Phone.Should().Be("+962 6 000 0000");
        state.Result.Latitude.Should().Be(31.95);
        state.Result.Url.Should().Be("https://abcstore.com");
        (await storeRepo.GetByNameAsync("ABC Store")).Should().NotBeNull("the verified profile is persisted");
        state.Events.Should().Contain(e => e.EventType == "profile.store");
        state.Events.Should().Contain(e => e.EventType == "store.verify");
    }

    [Fact]
    public async Task StoreResearchWorkflow_SubmitsCatalogCrawlToEdge_WhenEnabled()
    {
        // Cloudflare execution path (docs/architecture/cloudflare-workers-pipeline.md, Phase 1a): with
        // the layer configured and the admin flag on, the catalogue crawl is SUBMITTED to the
        // scrape-worker and the sub-workflow moves on — no inline Context.dev call, no rows written
        // here (the poll-drain service persists them whenever the edge job lands).
        var cf = new FakeCloudflareClient();
        var priceRepo = new InMemoryScrapedPriceRepo();
        using var provider = BuildProvider(s =>
        {
            s.AddSingleton<Daleel.Web.Cloudflare.ICloudflareWorkerClient>(cf);
            s.AddSingleton<ISystemConfigService>(new FakeSystemConfig(
                bools: new() { [Daleel.Web.Cloudflare.CloudflareWorkerOptions.EnabledFlag] = true }));
            s.AddSingleton<IScrapedPriceRepository>(priceRepo);
        });

        var state = await RunStoreAsync(provider,
            new StoreInfo { Name = "ABC Store", Url = "https://www.abcstore.com" });

        cf.Submits.Should().ContainSingle(s => s.Domain == "abcstore.com" && s.Store == "ABC Store",
            "the crawl is handed to the edge with the bare registrable domain");
        (await priceRepo.CountAsync()).Should().Be(0, "persistence belongs to the drain service now");
        state.Events.Should().Contain(e => e.EventType == "store.prices.submitted");
    }

    [Fact]
    public async Task StoreResearchWorkflow_StaysInline_WhenEdgeFlagIsOff()
    {
        var cf = new FakeCloudflareClient();
        using var provider = BuildProvider(s =>
        {
            s.AddSingleton<Daleel.Web.Cloudflare.ICloudflareWorkerClient>(cf);
            s.AddSingleton<ISystemConfigService>(new FakeSystemConfig(bools: new()));
        });

        var state = await RunStoreAsync(provider,
            new StoreInfo { Name = "ABC Store", Url = "https://www.abcstore.com" });

        cf.Submits.Should().BeEmpty("the admin flag is the strangler-fig switch — off means inline");
        state.Events.Should().NotContain(e => e.EventType == "store.prices.submitted");
    }

    // ── Item ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ItemDeepDiveWorkflow_ReusesSavedSpecs_WithoutScraping()
    {
        var key = ProductProfile.KeyFor("Samsung", "AR24", "Samsung AR24");
        var productRepo = new InMemoryProductRepo();
        await productRepo.UpsertAsync(new ProductProfile
        {
            Name = "Samsung AR24", Brand = "Samsung", Model = "AR24", NameKey = key,
            Details = "cooling: 24000 BTU", LastRefreshed = Now
        });

        using var provider = BuildProvider(s => s.AddSingleton<IProductProfileRepository>(productRepo));

        var model = new ProductModel { Name = "Samsung AR24", Brand = "Samsung", Model = "AR24" };
        var state = await RunItemAsync(provider, model);

        state.Result.Specs.Should().ContainKey("details");
        state.Result.Specs["details"].Should().Be("cooling: 24000 BTU");
        state.Events.Should().Contain(e => e.EventType == "item.reuse");
        state.Events.Should().Contain(e => e.EventType == "item.compare");
        (await productRepo.CountAsync()).Should().Be(1, "a reused profile is not re-written");
    }

    [Fact]
    public async Task ItemDeepDiveWorkflow_PersistsCanonicalSpecs_EvenWithoutAFreshScrape()
    {
        // An item that already carries specs is never scraped (so the old save step bailed and wrote
        // nothing) — yet its detail page reads only from the DB. The deep-dive must now persist the merged
        // canonical sheet so the page renders instead of showing "Details not yet available".
        var productRepo = new InMemoryProductRepo();
        using var provider = BuildProvider(s => s.AddSingleton<IProductProfileRepository>(productRepo));

        var model = new ProductModel
        {
            Name = "Samsung AR24", Brand = "Samsung", Model = "AR24",
            Specs = new Dictionary<string, string>
            {
                ["cooling"] = "24000 BTU", ["energy"] = "A++", ["type"] = "Split"
            }
        };
        var state = await RunItemAsync(provider, model);

        state.ReusedFromCache.Should().BeFalse("nothing was saved before this run");
        state.MergedSpecs.Should().NotBeNull("the spec pipeline merged the store specs into a canonical sheet");

        var saved = await productRepo.GetByKeyAsync(ProductProfile.KeyFor("Samsung", "AR24", "Samsung AR24"));
        saved.Should().NotBeNull("every enriched item is persisted so its detail page reads from the DB");
        saved!.SpecsJson.Should().NotBeNullOrEmpty();
        saved.SpecsJson.Should().Contain("24000 BTU");
        (await productRepo.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task ItemDeepDiveWorkflow_PersistsOfferPrices_ToScrapedPriceSeries()
    {
        // The offers were aggregated upstream; the deep-dive must durably record each priced one as a
        // ScrapedPrice observation (keyed by the item's normalized product key) so the per-product price
        // comparison reads it back — not just emit a telemetry event and discard them.
        var priceRepo = new InMemoryScrapedPriceRepo();
        using var provider = BuildProvider(s => s.AddSingleton<IScrapedPriceRepository>(priceRepo));

        var model = new ProductModel
        {
            Name = "Samsung AR24", Brand = "Samsung", Model = "AR24",
            Specs = new Dictionary<string, string> { ["cooling"] = "24000 BTU" },
            Offers = new[]
            {
                new PriceOffer { Source = "ABC Store", Price = 599m, Currency = "JOD", Url = "https://abc.com/ar24" },
                new PriceOffer { Source = "XYZ Store", Price = 575m, Currency = "JOD", Url = "https://xyz.com/ar24" },
                new PriceOffer { Source = "NoPriceStore", Price = null } // availability-only → must be skipped
            }
        };
        var state = await RunItemAsync(provider, model);

        var key = ProductProfile.KeyFor("Samsung", "AR24", "Samsung AR24");
        var saved = await priceRepo.LatestForProductAsync(key);
        saved.Should().HaveCount(2, "each priced offer is one observation; the price-less offer is skipped");
        saved.Select(p => p.StoreName).Should().BeEquivalentTo(new[] { "ABC Store", "XYZ Store" });
        saved.Should().OnlyContain(p => p.ProductKey == key && p.Provider == "pipeline");
        state.Events.Should().Contain(e => e.EventType == "item.compare");
    }

    [Fact]
    public async Task ItemDeepDiveWorkflow_CreatesBrandModel_WhenUnidentified()
    {
        // The listing matched no pre-existing catalogue model (NoOp identifier), but its merged canonical
        // sheet must still be persisted to a BrandModel — created on the fly — so the model DB is queryable
        // for store-discovered items the catalogue crawler hasn't harvested yet.
        var modelRepo = new InMemoryBrandModelRepo();
        using var provider = BuildProvider(s => s.AddSingleton<IBrandModelRepository>(modelRepo));

        var model = new ProductModel
        {
            Name = "Samsung AR24", Brand = "Samsung", Model = "AR24",
            Specs = new Dictionary<string, string>
            {
                ["cooling"] = "24000 BTU", ["energy"] = "A++", ["type"] = "Split"
            }
        };
        var state = await RunItemAsync(provider, model);

        state.MergedSpecs.Should().NotBeNull("the spec pipeline produced a canonical sheet to persist");
        state.IdentifiedBrandModelId.Should().NotBeNull("creating the BrandModel backfills the id onto the state");

        var created = await modelRepo.GetByIdAsync(state.IdentifiedBrandModelId!.Value);
        created.Should().NotBeNull("an unidentified item still gets a BrandModel so its specs are queryable");
        created!.ModelName.Should().Be("AR24");
        created.FinalSpecsJson.Should().Contain("24000 BTU");
        (await modelRepo.CountAsync()).Should().Be(1);
    }

    // ── Dispatcher fan-out (nested runner in child scopes, in parallel) ──────────────

    [Fact]
    public async Task RunManyAsync_DispatchesBrandsInParallel_PreservingOrder()
    {
        var researcher = new FakeResearcher
        {
            Brand = name => new Brand
            {
                Name = name, NameKey = Brand.Normalize(name), ReputationScore = 8,
                Website = $"https://{name.ToLowerInvariant()}.com"
            }
        };
        using var provider = BuildProvider(s =>
        {
            s.AddSingleton<IProfileResearcher>(researcher);
            s.AddSingleton<IBrandRepository>(new InMemoryBrandRepo());
        });
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var brands = new[]
        {
            new BrandInfo { Name = "Samsung" }, new BrandInfo { Name = "LG" }, new BrandInfo { Name = "Gree" }
        };

        var results = await SubWorkflowDispatcher
            .RunManyAsync<BrandResearchWorkflow, BrandResearchState, BrandInfo>(
                scopeFactory, brands,
                (st, svc, b) => { svc.Agent = BuildAgent(); st.Geo = "jordan"; st.Brand = b; st.Result = b; },
                progress: null,
                SubWorkflowDispatcher.DefaultTimeout, CancellationToken.None);

        results.Should().HaveCount(3);
        results.Select(r => r.Result.Name).Should().ContainInOrder("Samsung", "LG", "Gree");
        results.Should().OnlyContain(r => r.Result.Reputation != null);
        results.Should().OnlyContain(r => r.Result.Url != null);
    }

    // ── Harness ──────────────────────────────────────────────────────────────────────

    private static async Task<BrandResearchState> RunBrandAsync(ServiceProvider provider, BrandInfo brand)
    {
        using var scope = provider.CreateScope();
        var state = scope.ServiceProvider.GetRequiredService<BrandResearchState>();
        scope.ServiceProvider.GetRequiredService<SubWorkflowServices>().Agent = BuildAgent();
        state.Geo = "jordan";
        state.Brand = brand;
        state.Result = brand;
        await RunWorkflowAsync<BrandResearchWorkflow>(scope);
        return state;
    }

    private static async Task<StoreResearchState> RunStoreAsync(ServiceProvider provider, StoreInfo store)
    {
        using var scope = provider.CreateScope();
        var state = scope.ServiceProvider.GetRequiredService<StoreResearchState>();
        scope.ServiceProvider.GetRequiredService<SubWorkflowServices>().Agent = BuildAgent();
        state.Geo = "jordan";
        state.Store = store;
        state.Result = store;
        await RunWorkflowAsync<StoreResearchWorkflow>(scope);
        return state;
    }

    private static async Task<ItemDeepDiveState> RunItemAsync(ServiceProvider provider, ProductModel model)
    {
        using var scope = provider.CreateScope();
        var state = scope.ServiceProvider.GetRequiredService<ItemDeepDiveState>();
        scope.ServiceProvider.GetRequiredService<SubWorkflowServices>().Agent = BuildAgent();
        state.Geo = "jordan";
        state.Model = model;
        state.Result = model;
        await RunWorkflowAsync<ItemDeepDiveWorkflow>(scope);
        return state;
    }

    private static async Task RunWorkflowAsync<TWorkflow>(IServiceScope scope) where TWorkflow : IWorkflow, new()
    {
        var runner = scope.ServiceProvider.GetRequiredService<IWorkflowRunner>();
        var run = await runner.RunAsync(new TWorkflow(), cancellationToken: default);
        run.WorkflowState.Status.Should().Be(WorkflowStatus.Finished);
    }

    private static AgentService BuildAgent() =>
        new(new FixedLlm("{}"), new AgentOptions { DefaultGeo = "jordan" });

    private static ServiceProvider BuildProvider(Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddElsa(elsa => elsa.AddActivitiesFrom<Daleel.Web.Pipeline.SearchWorkflow>());
        services.AddScoped<BrandResearchState>();
        services.AddScoped<StoreResearchState>();
        services.AddScoped<ItemDeepDiveState>();
        services.AddScoped<SubWorkflowServices>();
        services.AddSingleton(new ProfileOptions { Now = () => Now });
        // Defaults so each test only overrides what it exercises.
        services.AddSingleton<IProfileResearcher>(new FakeResearcher());
        services.AddSingleton<IBrandRepository>(new InMemoryBrandRepo());
        services.AddSingleton<IStoreRepository>(new InMemoryStoreRepo());
        services.AddSingleton<IProductProfileRepository>(new InMemoryProductRepo());
        services.AddSingleton<IScrapedPriceRepository>(new InMemoryScrapedPriceRepo());
        services.AddSingleton<IBrandModelRepository>(new InMemoryBrandModelRepo());
        services.AddSingleton<IAgentFactory>(new FakeAgentFactory());
        // Smart-identification dependencies the item deep-dive now resolves. A no-op identifier keeps these
        // sequencing tests focused on the original behavior; the identifier itself is covered separately.
        services.AddSingleton<IProductIdentifier>(new NoOpProductIdentifier());
        services.AddSingleton<ISpecMerger>(new SpecMerger());
        services.AddSingleton<IR2StorageService>(new NullR2StorageService());
        configure(services);
        return services.BuildServiceProvider();
    }

    // ── Fakes ────────────────────────────────────────────────────────────────────────

    // A configured R2 that "hosts" any image by echoing back a deterministic public URL under the prefix.
    // Only StoreImageAsync is exercised by these tests; the rest satisfy the interface.
    private sealed class FakeImageR2 : IR2StorageService
    {
        public bool IsConfigured => true;

        public Task<R2BucketHealth> ProbeBucketAsync(R2Bucket bucket, CancellationToken ct = default) =>
            Task.FromResult(new R2BucketHealth(bucket, "fake", Reachable: true, HasObjects: true, PublicUrl: null, Error: null));

        public Task<string?> StoreImageAsync(string? sourceUrl, string keyPrefix, CancellationToken ct = default) =>
            Task.FromResult<string?>($"https://images.test/{keyPrefix}/logo.png");

        public Task<string?> StoreJsonAsync(string json, string objectKey, R2Bucket bucket = R2Bucket.Specs, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public Task<R2Listing> ListObjectsAsync(string? prefix, string? continuationToken = null, int maxKeys = 200, R2Bucket bucket = R2Bucket.Data, CancellationToken ct = default) =>
            Task.FromResult(R2Listing.Empty);

        public Task<R2ObjectText?> ReadTextAsync(string key, long maxBytes = 256 * 1024, R2Bucket bucket = R2Bucket.Data, CancellationToken ct = default) =>
            Task.FromResult<R2ObjectText?>(null);

        public string? DownloadUrl(string key, R2Bucket bucket = R2Bucket.Data, TimeSpan? expiry = null) => null;
    }

    private sealed class FixedLlm : ILlmClient
    {
        private readonly string _response;
        public FixedLlm(string response) => _response = response;
        public string Provider => "fake";
        public Task<LlmResponse> CompleteAsync(
            string systemPrompt, IReadOnlyList<LlmMessage> messages, CancellationToken ct = default) =>
            Task.FromResult(new LlmResponse { Content = _response });
    }

    private sealed class FakeResearcher : IProfileResearcher
    {
        private int _brandCalls;
        public Func<string, Brand?> Brand { get; init; } = _ => null;
        public Func<string, Store?> Store { get; init; } = _ => null;
        public bool IsAvailable { get; init; } = true;
        public int BrandCalls => _brandCalls;

        public Task<Brand?> ResearchBrandAsync(string brandName, string? geo, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _brandCalls);
            return Task.FromResult(Brand(brandName));
        }

        public Task<Store?> ResearchStoreAsync(string storeName, string? geo, CancellationToken ct = default) =>
            Task.FromResult(Store(storeName));
    }

    private sealed class InMemoryBrandRepo : IBrandRepository
    {
        private readonly ConcurrentDictionary<string, Brand> _store = new();

        public Task<Brand?> GetByNameAsync(string name, CancellationToken ct = default) =>
            Task.FromResult(_store.TryGetValue(Brand.Normalize(name), out var b) ? b : null);

        public Task<Brand?> GetByIdAsync(int id, CancellationToken ct = default) =>
            Task.FromResult(_store.Values.FirstOrDefault(b => b.Id == id));

        public Task<Brand> UpsertAsync(Brand brand, CancellationToken ct = default)
        {
            var key = string.IsNullOrEmpty(brand.NameKey) ? Brand.Normalize(brand.Name) : brand.NameKey;
            brand.NameKey = key;
            _store[key] = brand;
            return Task.FromResult(brand);
        }

        public Task<IReadOnlyList<Brand>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Brand>>(_store.Values.ToList());

        public Task<IReadOnlyList<Brand>> ListStaleAsync(DateTimeOffset olderThan, int max, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Brand>>(Array.Empty<Brand>());

        public Task<int> CountAsync(CancellationToken ct = default) => Task.FromResult(_store.Count);
    }

    private sealed class InMemoryStoreRepo : IStoreRepository
    {
        private readonly ConcurrentDictionary<string, Store> _store = new();

        public Task<Store?> GetByNameAsync(string name, CancellationToken ct = default) =>
            Task.FromResult(_store.TryGetValue(Store.Normalize(name), out var s) ? s : null);

        public Task<Store?> GetByIdAsync(int id, CancellationToken ct = default) =>
            Task.FromResult(_store.Values.FirstOrDefault(s => s.Id == id));

        public Task<Store> UpsertAsync(Store store, CancellationToken ct = default)
        {
            var key = string.IsNullOrEmpty(store.NameKey) ? Store.Normalize(store.Name) : store.NameKey;
            store.NameKey = key;
            _store[key] = store;
            return Task.FromResult(store);
        }

        public Task<IReadOnlyList<Store>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Store>>(_store.Values.ToList());

        public Task<IReadOnlyList<Store>> ListStaleAsync(DateTimeOffset olderThan, int max, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Store>>(Array.Empty<Store>());

        public Task<int> CountAsync(CancellationToken ct = default) => Task.FromResult(_store.Count);
    }

    private sealed class InMemoryProductRepo : IProductProfileRepository
    {
        private readonly ConcurrentDictionary<string, ProductProfile> _store = new();

        public Task<ProductProfile?> GetByKeyAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(_store.TryGetValue(key, out var p) ? p : null);

        public Task<ProductProfile> UpsertAsync(ProductProfile profile, CancellationToken ct = default)
        {
            var key = string.IsNullOrEmpty(profile.NameKey)
                ? ProductProfile.KeyFor(profile.Brand, profile.Model, profile.Name)
                : profile.NameKey;
            profile.NameKey = key;
            _store[key] = profile;
            return Task.FromResult(profile);
        }

        public Task<int> CountAsync(CancellationToken ct = default) => Task.FromResult(_store.Count);
    }

    private sealed class InMemoryScrapedPriceRepo : IScrapedPriceRepository
    {
        private readonly List<ScrapedPrice> _rows = new();

        public Task AddAsync(ScrapedPrice price, CancellationToken ct = default)
        {
            lock (_rows) { _rows.Add(price); }
            return Task.CompletedTask;
        }

        public Task AddRangeAsync(IReadOnlyCollection<ScrapedPrice> prices, CancellationToken ct = default)
        {
            lock (_rows) { _rows.AddRange(prices); }
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ScrapedPrice>> LatestForProductAsync(string productKey, CancellationToken ct = default)
        {
            lock (_rows)
            {
                var latest = _rows
                    .Where(p => p.ProductKey == productKey)
                    .GroupBy(p => p.StoreName, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.OrderByDescending(p => p.ScrapedAt).First())
                    .ToList();
                return Task.FromResult<IReadOnlyList<ScrapedPrice>>(latest);
            }
        }

        public Task<IReadOnlyList<ScrapedPrice>> LatestForStoreAsync(string storeName, CancellationToken ct = default)
        {
            lock (_rows)
            {
                var latest = _rows
                    .Where(p => string.Equals(p.StoreName, storeName, StringComparison.OrdinalIgnoreCase))
                    .GroupBy(p => p.ProductKey, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.OrderByDescending(p => p.ScrapedAt).First())
                    .ToList();
                return Task.FromResult<IReadOnlyList<ScrapedPrice>>(latest);
            }
        }

        public Task<IReadOnlyList<ScrapedPrice>> HistoryForProductAsync(string productKey, int max = 500, CancellationToken ct = default)
        {
            lock (_rows)
            {
                var history = _rows
                    .Where(p => p.ProductKey == productKey)
                    .OrderByDescending(p => p.ScrapedAt)
                    .Take(max)
                    .ToList();
                return Task.FromResult<IReadOnlyList<ScrapedPrice>>(history);
            }
        }

        public Task<int> CountAsync(CancellationToken ct = default)
        {
            lock (_rows) { return Task.FromResult(_rows.Count); }
        }
    }

    private sealed class InMemoryBrandModelRepo : IBrandModelRepository
    {
        private readonly List<BrandModel> _models = new();
        private int _nextId;

        public Task<BrandModel> UpsertAsync(BrandModel model, CancellationToken ct = default)
        {
            var key = string.IsNullOrEmpty(model.ModelKey) ? BrandModel.Normalize(model.ModelName) : model.ModelKey;
            model.ModelKey = key;
            lock (_models)
            {
                var existing = _models.FirstOrDefault(m => m.BrandId == model.BrandId && m.ModelKey == key);
                if (existing is not null)
                {
                    existing.FinalSpecsJson = model.FinalSpecsJson ?? existing.FinalSpecsJson;
                    existing.FinalSpecsR2Url = model.FinalSpecsR2Url ?? existing.FinalSpecsR2Url;
                    existing.Category = model.Category ?? existing.Category;
                    existing.ImageUrl = model.ImageUrl ?? existing.ImageUrl;
                    existing.LastRefreshed = model.LastRefreshed;
                    return Task.FromResult(existing);
                }

                model.Id = ++_nextId;
                _models.Add(model);
                return Task.FromResult(model);
            }
        }

        public Task<IReadOnlyList<BrandModel>> ListByBrandAsync(int brandId, CancellationToken ct = default)
        {
            lock (_models)
            {
                return Task.FromResult<IReadOnlyList<BrandModel>>(_models.Where(m => m.BrandId == brandId).ToList());
            }
        }

        public Task<BrandModel?> GetByBrandAndKeyAsync(int brandId, string modelKey, CancellationToken ct = default)
        {
            lock (_models)
            {
                return Task.FromResult(_models.FirstOrDefault(m => m.BrandId == brandId && m.ModelKey == modelKey));
            }
        }

        public Task<BrandModel?> GetByIdAsync(int id, CancellationToken ct = default)
        {
            lock (_models) { return Task.FromResult(_models.FirstOrDefault(m => m.Id == id)); }
        }

        public Task SaveFinalSpecsAsync(int id, string? finalSpecsJson, string? finalSpecsR2Url, CancellationToken ct = default)
        {
            lock (_models)
            {
                var existing = _models.FirstOrDefault(m => m.Id == id);
                if (existing is not null)
                {
                    existing.FinalSpecsJson = finalSpecsJson ?? existing.FinalSpecsJson;
                    existing.FinalSpecsR2Url = finalSpecsR2Url ?? existing.FinalSpecsR2Url;
                }
            }

            return Task.CompletedTask;
        }

        public Task<BrandModel?> FindByProductKeyAsync(string productKey, CancellationToken ct = default)
        {
            lock (_models)
            {
                return Task.FromResult(_models.FirstOrDefault(m => ProductProfile.Normalize(m.ModelName) == productKey));
            }
        }

        public Task<int> CountForBrandAsync(int brandId, CancellationToken ct = default)
        {
            lock (_models) { return Task.FromResult(_models.Count(m => m.BrandId == brandId)); }
        }

        public Task<int> CountAsync(CancellationToken ct = default)
        {
            lock (_models) { return Task.FromResult(_models.Count); }
        }
    }

    // A no-op identifier: every listing is "unidentified", so the spec pipeline runs on store specs only —
    // preserving the pre-identification behavior these sequencing tests assert.
    private sealed class NoOpProductIdentifier : IProductIdentifier
    {
        public Task<ProductIdentification> IdentifyAsync(ProductModel item, CancellationToken ct = default) =>
            Task.FromResult(ProductIdentification.None);
    }

    // Resolve returns null so the store ScrapePrices step (which needs CONTEXT_DEV_API_KEY) no-ops.
    private sealed class FakeAgentFactory : IAgentFactory
    {
        public bool HasLlm(IReadOnlyDictionary<string, string>? keys = null) => false;
        public ProviderStatus Describe(IReadOnlyDictionary<string, string>? keys = null) => throw new NotSupportedException();
        public AgentService Build(AgentRequest request) => throw new NotSupportedException();
        public ILlmClient? TryBuildLlm(string? model = null, IReadOnlyDictionary<string, string>? keys = null) => null;
        public string? Resolve(string name, IReadOnlyDictionary<string, string>? keys = null) => null;
    }

    private sealed class FakeCloudflareClient : Daleel.Web.Cloudflare.ICloudflareWorkerClient
    {
        public List<(string Domain, string? Store, string? SearchJobId, int MaxProducts)> Submits { get; } = new();

        public Task<Daleel.Web.Cloudflare.WorkerHandle?> SubmitCatalogAsync(
            string domain, string? store, string? searchJobId, int maxProducts = 0, CancellationToken ct = default)
        {
            Submits.Add((domain, store, searchJobId, maxProducts));
            return Task.FromResult<Daleel.Web.Cloudflare.WorkerHandle?>(
                new Daleel.Web.Cloudflare.WorkerHandle { JobId = "j1", ResultKey = $"test/{domain}.json" });
        }

        public Task<Daleel.Web.Cloudflare.WorkerJobStatus?> GetJobStatusAsync(string jobId, CancellationToken ct = default) =>
            Task.FromResult<Daleel.Web.Cloudflare.WorkerJobStatus?>(null);

        public Task<T?> ReadResultAsync<T>(string resultKey, CancellationToken ct = default) where T : class =>
            Task.FromResult<T?>(null);
    }

    /// <summary>Dictionary-backed ISystemConfigService — unset keys fall back like production.</summary>
    private sealed class FakeSystemConfig : ISystemConfigService
    {
        private readonly Dictionary<string, bool> _bools;
        public FakeSystemConfig(Dictionary<string, bool> bools) => _bools = bools;

        public Task<string?> GetAsync(string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<int> GetIntAsync(string key, int fallback, CancellationToken ct = default) => Task.FromResult(fallback);
        public Task<bool> GetBoolAsync(string key, bool fallback, CancellationToken ct = default) =>
            Task.FromResult(_bools.TryGetValue(key, out var v) ? v : fallback);
        public Task SetAsync(string key, string value, string type = "string", CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task<IReadOnlyList<SystemConfig>> AllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SystemConfig>>(Array.Empty<SystemConfig>());
        public Task SeedDefaultsAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
