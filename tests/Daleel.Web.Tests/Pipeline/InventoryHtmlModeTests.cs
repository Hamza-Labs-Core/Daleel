using System.Collections.Concurrent;
using System.Net;
using Daleel.Agent;
using Daleel.Core.Geo;
using Daleel.Core.Models;
using Daleel.Web.Data;
using Daleel.Web.Persistence;
using Daleel.Web.Pipeline.Enrichment;
using Daleel.Web.Pipeline.Inventory;
using Daleel.Web.Storage;
using Daleel.Web.Tests.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Daleel.Web.Tests.Pipeline;

/// <summary>
/// The inventory monitor's HTML mode: sitemap parsing + listing/product URL classification, the
/// sync's fall-through fan-out when no machine-readable catalogue probes, the LLM extraction mapping
/// into <see cref="InventoryListing"/>, and the per-listing-page walk (upsert, hash-skip, loop safety).
/// </summary>
public sealed class InventoryHtmlModeTests : IDisposable
{
    private readonly PostgresTestContext _pg = new();
    private readonly InMemoryR2 _r2 = new();

    public void Dispose() => _pg.Dispose();

    // ── Sitemap parsing + URL classification ─────────────────────────────────

    [Fact]
    public void SitemapParse_ReadsIndexChildren_AndUrlsetUrls()
    {
        const string index = """
        <?xml version="1.0" encoding="UTF-8"?>
        <sitemapindex xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
          <sitemap><loc>https://shop.jo/product_cat-sitemap.xml</loc></sitemap>
          <sitemap><loc>https://shop.jo/post-sitemap.xml</loc></sitemap>
        </sitemapindex>
        """;
        const string urlset = """
        <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
          <url><loc>https://shop.jo/product-category/tvs/</loc></url>
          <url><loc>https://shop.jo/product/oled55c4/</loc></url>
        </urlset>
        """;

        var parsedIndex = SitemapXml.Parse(index);
        parsedIndex.Sitemaps.Should().HaveCount(2);
        parsedIndex.Urls.Should().BeEmpty();

        var parsedUrlset = SitemapXml.Parse(urlset);
        parsedUrlset.Sitemaps.Should().BeEmpty();
        parsedUrlset.Urls.Should().Equal(
            "https://shop.jo/product-category/tvs/", "https://shop.jo/product/oled55c4/");

        var notASitemap = SitemapXml.Parse("<html>bot wall, not xml</html>");
        notASitemap.Sitemaps.Should().BeEmpty();
        notASitemap.Urls.Should().BeEmpty();
        SitemapXml.Parse("prose before <urlset><url><loc>https://x.jo/category/a</loc></url></urlset>")
            .Urls.Should().ContainSingle("parsing starts at the first angle bracket (browser-rendered fetch)");
    }

    [Theory]
    [InlineData("https://shop.jo/product-category/tvs/", true, false)]
    [InlineData("https://shop.jo/collections/air-conditioners", true, false)]
    [InlineData("https://shop.jo/category/mobiles", true, false)]
    [InlineData("https://shop.jo/shop/deals", true, false)]
    [InlineData("https://shop.jo/products/gree-ac-12000", false, true)]
    [InlineData("https://shop.jo/product/oled55c4", false, true)]
    [InlineData("https://shop.jo/collections/tvs/products/oled55c4", false, true)]
    [InlineData("https://shop.jo/blog/best-tvs-2026", false, false)]
    public void UrlClassifier_SeparatesListingsFromProducts(string url, bool listing, bool product)
    {
        CatalogUrlClassifier.IsListingUrl(url).Should().Be(listing);
        CatalogUrlClassifier.IsProductUrl(url).Should().Be(product);
    }

    [Fact]
    public void UrlClassifier_SkipsBlogAndMediaSitemaps_PrefersCategoryOnes()
    {
        CatalogUrlClassifier.IsUsefulChildSitemap("https://shop.jo/post-sitemap.xml").Should().BeFalse();
        CatalogUrlClassifier.IsUsefulChildSitemap("https://shop.jo/product_cat-sitemap.xml").Should().BeTrue();
        CatalogUrlClassifier.LooksLikeCategorySitemap("https://shop.jo/product_cat-sitemap.xml").Should().BeTrue();
        CatalogUrlClassifier.LooksLikeCategorySitemap("https://shop.jo/product-sitemap.xml").Should().BeFalse();
    }

    [Fact]
    public async Task Discovery_WalksSitemapIndex_ToCategoryUrls_WithoutAnyLlm()
    {
        var http = new HttpClient(new FakeHttp(new Dictionary<string, string>
        {
            ["https://shop.jo/sitemap.xml"] = """
                <sitemapindex><sitemap><loc>https://shop.jo/product_cat-sitemap.xml</loc></sitemap>
                <sitemap><loc>https://shop.jo/post-sitemap.xml</loc></sitemap></sitemapindex>
                """,
            ["https://shop.jo/product_cat-sitemap.xml"] = """
                <urlset><url><loc>https://shop.jo/product-category/tvs/</loc></url>
                <url><loc>https://shop.jo/product-category/acs/</loc></url>
                <url><loc>https://shop.jo/product/oled55c4/</loc></url>
                <url><loc>https://other-site.com/product-category/x/</loc></url></urlset>
                """
        }));
        using var discovery = new HtmlCatalogDiscovery(providers: null, http: http);

        var pages = await discovery.DiscoverListingPagesAsync("shop.jo", agent: null);

        pages.Should().Equal(
            "https://shop.jo/product-category/tvs/", "https://shop.jo/product-category/acs/");
    }

    // ── Sync fall-through fan-out ────────────────────────────────────────────

    [Fact]
    public async Task Sync_FallsToHtmlMode_FanningOutOneUnitPerListingPage()
    {
        var sp = Build(new NullCatalog(), discoveredListings: new[]
        {
            "https://shop.jo/product-category/tvs/", "https://shop.jo/product-category/acs/"
        });
        var (job, store) = await SeedJobAsync(sp);
        var queue = new RecordingQueue();

        var outcome = await new InventorySyncHandler(NullLogger<InventorySyncHandler>.Instance)
            .ExecuteAsync(Unit(job, EnrichmentUnit.InventorySync, SyncPayload(store)), Ctx(sp, job, queue), default);

        outcome.Should().BeOfType<UnitOutcome.Done>("no machine-readable catalogue must FALL to HTML mode, never Kill");
        var pages = queue.Enqueued.Where(i => i.Kind == EnrichmentUnit.InventoryHtmlPage).ToList();
        pages.Should().HaveCount(2, "one unit per LISTING page — category pages carry many products");
        queue.Enqueued.Should().ContainSingle(i => i.Kind == EnrichmentUnit.InventoryFinalize);
        EnrichmentWorkQueue.ReadPayload<InventoryHtmlPagePayload>(pages[0].Payload)!
            .Url.Should().Be("https://shop.jo/product-category/tvs/");
    }

    [Fact]
    public async Task Sync_NoDiscoverableCatalogue_KillsVisibly()
    {
        var sp = Build(new NullCatalog(), discoveredListings: Array.Empty<string>());
        var (job, store) = await SeedJobAsync(sp);

        var outcome = await new InventorySyncHandler(NullLogger<InventorySyncHandler>.Instance)
            .ExecuteAsync(Unit(job, EnrichmentUnit.InventorySync, SyncPayload(store)),
                Ctx(sp, job, new RecordingQueue()), default);

        outcome.Should().BeOfType<UnitOutcome.Kill>()
            .Which.Reason.Should().Contain("no discoverable catalogue");
    }

    // ── Extraction mapping ───────────────────────────────────────────────────

    private const string CannedListingReply = """
        {
          "products": [
            {"name":"LG OLED55C4 TV","brand":"LG","model":"OLED55C4","url":"/product/oled55c4",
             "imageUrl":"/img/c4.jpg","price":"1,299.00","currency":"JOD","availability":"in stock"},
            {"name":"Old Fan","brand":null,"model":null,"url":"/product/old-fan",
             "imageUrl":null,"price":null,"currency":null,"availability":"out of stock"}
          ],
          "nextPageUrl": null,
          "hasLoadMore": false,
          "totalPages": 1
        }
        """;

    [Fact]
    public async Task Extraction_MapsCrawlProducts_IntoInventoryListings()
    {
        var llm = new CountingLlm(CannedListingReply);
        var agent = new AgentService(llm);

        var result = await agent.ExtractStoreCatalogPageAsync(
            "# TVs\nproduct grid markdown…", "https://shop.jo/product-category/tvs/",
            GeoProfiles.ResolveOrDefault("jordan"));
        var listings = InventoryHtmlPageHandler.MapListings(result.Products);

        listings.Should().HaveCount(2);
        var tv = listings[0];
        tv.Name.Should().Be("LG OLED55C4 TV");
        tv.Brand.Should().Be("LG");
        tv.Sku.Should().Be("OLED55C4", "the crawler's MODEL slot carries the identity the pipeline keys on");
        tv.Price.Should().Be(1299.00m, "string prices with thousands separators parse");
        tv.Available.Should().BeTrue();
        tv.Url.Should().Be("https://shop.jo/product/oled55c4", "URLs absolutize against the page origin");
        tv.ImageUrl.Should().Be("https://shop.jo/img/c4.jpg");
        listings[1].Available.Should().BeFalse("\"out of stock\" maps to unavailable");
    }

    [Fact]
    public void Mapping_DefaultsToAvailable_WhenNoAvailabilityShown()
    {
        InventoryHtmlPageHandler.ToInventoryListing(new ProductListing { Name = "X" })
            .Available.Should().BeTrue("a card that doesn't say out-of-stock is buyable");
    }

    // ── The listing-page walk unit ───────────────────────────────────────────

    [Fact]
    public async Task HtmlPage_ExtractsAndUpserts_ThenHashSkips_WithZeroLlm()
    {
        const string url = "https://shop.jo/product-category/tvs/";
        var llm = new CountingLlm(CannedListingReply);
        var sp = Build(new NullCatalog(), providerPages: new Dictionary<string, string> { [url] = "# TVs\ngrid…" });
        var (job, store) = await SeedJobAsync(sp);
        var handler = new InventoryHtmlPageHandler(NullLogger<InventoryHtmlPageHandler>.Instance);
        var unit = Unit(job, EnrichmentUnit.InventoryHtmlPage,
            EnrichmentWorkQueue.Payload(new InventoryHtmlPagePayload(store.Id, "shop.jo", url, DateTimeOffset.UtcNow)));

        var first = await handler.ExecuteAsync(unit, Ctx(sp, job, new RecordingQueue(), llm), default);

        first.Should().BeOfType<UnitOutcome.Done>();
        var callsAfterExtract = llm.Calls;
        callsAfterExtract.Should().BeGreaterThan(0, "a CHANGED page is LLM-extracted");
        var db = sp.GetRequiredService<DaleelDbContext>();
        (await db.EntityRecords.CountAsync(r => r.MergedIntoId == null)).Should().Be(2);
        (await db.ScrapedPrices.CountAsync(r => r.Provider == "inventory" && r.StoreName == "shop.jo")).Should().Be(2);
        var row = await db.StoreCatalogPages.SingleAsync(x => x.Domain == "shop.jo" && x.Url == url);
        row.ContentHash.Should().NotBeEmpty();
        row.ProductKeysJson.Should().Contain("oled55c4", "the page remembers its product keys for hash-skip presence");

        // Second sync, unchanged content: presence advances, NOTHING is spent on the LLM.
        var before = (await db.ScrapedPrices.Where(r => r.StoreName == "shop.jo").ToListAsync())
            .Min(r => r.LastSeenAt);
        await Task.Delay(25); // the watermark stores at millisecond precision — let it tick
        var rerun = Unit(job, EnrichmentUnit.InventoryHtmlPage,
            EnrichmentWorkQueue.Payload(new InventoryHtmlPagePayload(store.Id, "shop.jo", url, DateTimeOffset.UtcNow)));
        (await handler.ExecuteAsync(rerun, Ctx(sp, job, new RecordingQueue(), llm), default))
            .Should().BeOfType<UnitOutcome.Done>();

        llm.Calls.Should().Be(callsAfterExtract, "an UNCHANGED page must cost zero LLM calls — hash-skip");
        var db2 = sp.GetRequiredService<DaleelDbContext>();
        (await db2.EntityRecords.CountAsync(r => r.MergedIntoId == null)).Should().Be(2, "no duplicates on re-sync");
        (await db2.ScrapedPrices.Where(r => r.StoreName == "shop.jo").ToListAsync())
            .Should().OnlyContain(r => r.LastSeenAt > before, "hash-skip still advances the presence watermark");
    }

    [Fact]
    public async Task HtmlPage_PaginationLoop_TerminatesOnVisitedUrl()
    {
        const string url = "https://shop.jo/product-category/tvs/";
        // The canned reply's nextPageUrl points BACK at the same page — the visited-set must stop the walk.
        var looping = CannedListingReply.Replace("\"nextPageUrl\": null", "\"nextPageUrl\": \"/product-category/tvs/\"");
        var llm = new CountingLlm(looping);
        var sp = Build(new NullCatalog(), providerPages: new Dictionary<string, string> { [url] = "# TVs\ngrid…" });
        var (job, store) = await SeedJobAsync(sp);

        var outcome = await new InventoryHtmlPageHandler(NullLogger<InventoryHtmlPageHandler>.Instance)
            .ExecuteAsync(
                Unit(job, EnrichmentUnit.InventoryHtmlPage,
                    EnrichmentWorkQueue.Payload(new InventoryHtmlPagePayload(store.Id, "shop.jo", url, DateTimeOffset.UtcNow))),
                Ctx(sp, job, new RecordingQueue(), llm), default);

        outcome.Should().BeOfType<UnitOutcome.Done>();
        llm.Calls.Should().Be(2, "one extraction + one pagination call — the loop never re-walks a visited URL");
    }

    [Fact]
    public async Task HtmlPage_UnreadableFirstPage_Retries()
    {
        var sp = Build(new NullCatalog(), providerPages: new Dictionary<string, string>());
        var (job, store) = await SeedJobAsync(sp);

        var outcome = await new InventoryHtmlPageHandler(NullLogger<InventoryHtmlPageHandler>.Instance)
            .ExecuteAsync(
                Unit(job, EnrichmentUnit.InventoryHtmlPage,
                    EnrichmentWorkQueue.Payload(new InventoryHtmlPagePayload(
                        store.Id, "shop.jo", "https://shop.jo/product-category/tvs/", DateTimeOffset.UtcNow))),
                Ctx(sp, job, new RecordingQueue(), new CountingLlm(CannedListingReply)), default);

        outcome.Should().BeOfType<UnitOutcome.Retry>();
    }

    // ── Harness ──────────────────────────────────────────────────────────────

    private static string SyncPayload(Store store) =>
        EnrichmentWorkQueue.Payload(new InventorySyncPayload(store.Id, "shop.jo", DateTimeOffset.UtcNow));

    private IServiceProvider Build(
        IStoreCatalogClient catalog,
        IReadOnlyList<string>? discoveredListings = null,
        Dictionary<string, string>? providerPages = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(catalog);
        services.AddSingleton<IHtmlCatalogDiscovery>(new StubDiscovery(discoveredListings ?? Array.Empty<string>()));
        services.AddSingleton<Daleel.Web.Services.IProviderApi>(new StubProviders(providerPages ?? new Dictionary<string, string>()));
        services.AddSingleton<IR2StorageService>(_r2);
        services.AddTransient(_ => _pg.NewContext());
        services.AddTransient<IEntityRecordRepository>(sp => new EntityRecordRepository(sp.GetRequiredService<DaleelDbContext>()));
        services.AddTransient<IBrandRepository>(sp => new BrandRepository(sp.GetRequiredService<DaleelDbContext>()));
        services.AddTransient<IStoreRepository>(sp => new StoreRepository(sp.GetRequiredService<DaleelDbContext>()));
        services.AddTransient<ISearchEntityStore>(sp => new SearchEntityStore(
            sp.GetRequiredService<IR2StorageService>(),
            sp.GetRequiredService<IEntityRecordRepository>(),
            sp.GetRequiredService<IBrandRepository>(),
            sp.GetRequiredService<IStoreRepository>(),
            NullLogger<SearchEntityStore>.Instance));
        return services.BuildServiceProvider();
    }

    private async Task<(SearchJob Job, Store Store)> SeedJobAsync(IServiceProvider sp)
    {
        var db = sp.GetRequiredService<DaleelDbContext>();
        var store = new Store
        {
            Name = "Shop JO", NameKey = Store.Normalize("Shop JO"), Website = "https://shop.jo",
            MonitorEnabled = true, LastRefreshed = DateTimeOffset.UtcNow
        };
        db.Stores.Add(store);
        var job = new SearchJob
        {
            UserId = "system:inventory", Query = "inventory:shop.jo", QueryType = "inventory",
            Geo = "jordan", Status = StoreInventoryMonitorService.JobStatusInventory,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.SearchJobs.Add(job);
        await db.SaveChangesAsync();
        return (job, store);
    }

    private static EnrichmentWorkItem Unit(SearchJob job, string kind, string payload) => new()
    {
        Id = 1, SearchJobId = job.Id, UserId = job.UserId, Kind = kind, Payload = payload, MaxAttempts = 3
    };

    private static EnrichmentUnitContext Ctx(
        IServiceProvider sp, SearchJob job, IEnrichmentWorkQueue queue, Daleel.Core.Llm.ILlmClient? llm = null) => new()
    {
        Services = sp,
        Job = job,
        Agent = () => llm is null ? null! : new AgentService(llm),
        Results = new NullResults(),
        Queue = queue
    };

    private sealed class NullCatalog : IStoreCatalogClient
    {
        public Task<(IReadOnlyList<InventoryListing> Listings, string RawPayload)?> GetPageAsync(
            string domain, int page, CancellationToken ct = default) =>
            Task.FromResult<(IReadOnlyList<InventoryListing>, string)?>(null);
    }

    private sealed class StubDiscovery(IReadOnlyList<string> listings) : IHtmlCatalogDiscovery
    {
        public Task<IReadOnlyList<string>> DiscoverListingPagesAsync(
            string domain, AgentService? agent, CancellationToken ct = default) => Task.FromResult(listings);
    }

    /// <summary>Counts completions and returns one canned reply — the extraction AND pagination
    /// prompts both read the fields they need from it.</summary>
    private sealed class CountingLlm(string reply) : Daleel.Core.Llm.ILlmClient
    {
        private int _calls;

        public int Calls => _calls;
        public string Provider => "stub";

        public Task<Daleel.Core.Llm.LlmResponse> CompleteAsync(
            string systemPrompt, IReadOnlyList<Daleel.Core.Llm.LlmMessage> messages, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new Daleel.Core.Llm.LlmResponse { Content = reply });
        }
    }

    /// <summary>Serves canned XML/text bodies by exact URL; 404 otherwise.</summary>
    private sealed class FakeHttp(Dictionary<string, string> bodies) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            return Task.FromResult(bodies.TryGetValue(url, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    /// <summary>IProviderApi stub: ScrapePageAsync serves canned markdown by URL; everything else is
    /// unconfigured/empty (the HTML mode touches only the scrape path).</summary>
    private sealed class StubProviders(Dictionary<string, string> pages) : Daleel.Web.Services.IProviderApi
    {
        public bool HasScraper => true;
        public bool HasEdge => false;
        public bool EdgeDrainReady => false;
        public bool HasPlaces => false;
        public bool HasSocial => false;
        public bool HasEdgeExtract => false;
        public bool HasEdgeClassify => false;
        public bool HasEdgeFilter => false;

        public Task<Daleel.Search.Abstractions.ScrapedPage?> ScrapePageAsync(
            string url, Daleel.Search.Abstractions.ScrapeFormat format = Daleel.Search.Abstractions.ScrapeFormat.Markdown,
            CancellationToken ct = default) =>
            Task.FromResult(pages.TryGetValue(url, out var content)
                ? new Daleel.Search.Abstractions.ScrapedPage { Url = url, Content = content, Success = true }
                : null);

        public Task<IReadOnlyList<Daleel.Search.Providers.CatalogProduct>> ExtractCatalogAsync(
            string domain, int maxProducts = 0, int timeoutMs = 45_000, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Daleel.Search.Providers.CatalogProduct>>(Array.Empty<Daleel.Search.Providers.CatalogProduct>());

        public Task<Daleel.Search.Providers.BrandProfile?> GetBrandAsync(string domain, CancellationToken ct = default) =>
            Task.FromResult<Daleel.Search.Providers.BrandProfile?>(null);

        public Task<IReadOnlyList<Daleel.Core.Models.StoreLocation>> SearchPlacesAsync(
            string query, Daleel.Core.Geo.GeoPoint? near = null, double radiusMeters = 5000,
            string? languageCode = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Daleel.Core.Models.StoreLocation>>(Array.Empty<Daleel.Core.Models.StoreLocation>());

        public Task<Daleel.Core.Models.StoreLocation?> GetPlaceDetailsAsync(string placeId, CancellationToken ct = default) =>
            Task.FromResult<Daleel.Core.Models.StoreLocation?>(null);

        public Task<IReadOnlyList<Daleel.Core.Models.SocialPost>> FetchSocialPostsAsync(
            Daleel.Core.Models.Source source, string? keyword = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Daleel.Core.Models.SocialPost>>(Array.Empty<Daleel.Core.Models.SocialPost>());

        public Task<Daleel.Web.Cloudflare.WorkerHandle?> SubmitEdgeCatalogAsync(
            string domain, string? store, string? searchJobId, int maxProducts = 0, CancellationToken ct = default) =>
            Task.FromResult<Daleel.Web.Cloudflare.WorkerHandle?>(null);

        public Task<Daleel.Web.Cloudflare.WorkerHandle?> SubmitEdgeBrandAsync(
            string domain, string brandName, string? searchJobId, bool refresh = false, CancellationToken ct = default) =>
            Task.FromResult<Daleel.Web.Cloudflare.WorkerHandle?>(null);

        public Task<IReadOnlyList<Daleel.Web.Cloudflare.ClassifyVerdict>> ClassifyTextAsync(
            IReadOnlyList<(string Id, string Text)> items, IReadOnlyList<string> labels, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Daleel.Web.Cloudflare.ClassifyVerdict>>(Array.Empty<Daleel.Web.Cloudflare.ClassifyVerdict>());

        public Task<IReadOnlyList<Daleel.Search.Providers.CatalogProduct>> ExtractProductsFromContentAsync(
            string content, string? market = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Daleel.Search.Providers.CatalogProduct>>(Array.Empty<Daleel.Search.Providers.CatalogProduct>());

        public Task<IReadOnlyList<Daleel.Web.Cloudflare.FilterFindingDto>> FilterTextFindingsAsync(
            IReadOnlyList<(string Id, string Text, string? SourceUrl)> items, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Daleel.Web.Cloudflare.FilterFindingDto>>(Array.Empty<Daleel.Web.Cloudflare.FilterFindingDto>());

        public Task<IReadOnlyList<Daleel.Web.Cloudflare.FilterFindingDto>> FilterImageFindingsAsync(
            IReadOnlyList<string> urls, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Daleel.Web.Cloudflare.FilterFindingDto>>(Array.Empty<Daleel.Web.Cloudflare.FilterFindingDto>());
    }

    private sealed class NullResults : IEnrichedResultStore
    {
        public Task<AgentAnswer?> LoadAsync(int jobId, CancellationToken ct = default) => Task.FromResult<AgentAnswer?>(null);
        public Task<bool> PatchAsync(EnrichmentWorkItem item, Func<AgentAnswer, AgentAnswer?> mutate, CancellationToken ct = default) =>
            Task.FromResult(false);
    }

    private sealed class RecordingQueue : IEnrichmentWorkQueue
    {
        public List<EnrichmentWorkItem> Enqueued { get; } = new();

        public Task EnqueueAsync(IReadOnlyList<EnrichmentWorkItem> items, CancellationToken ct = default)
        {
            Enqueued.AddRange(items);
            return Task.CompletedTask;
        }

        public Task<bool> EnqueueFanOutAsync(int searchJobId, string selfKind, IReadOnlyList<EnrichmentWorkItem> children, CancellationToken ct = default)
        {
            Enqueued.AddRange(children);
            return Task.FromResult(children.Count > 0);
        }

        public Task<IReadOnlyList<EnrichmentWorkItem>> ClaimAsync(int max, TimeSpan lease, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<EnrichmentWorkItem>>(Array.Empty<EnrichmentWorkItem>());
        public Task CompleteAsync(long id, CancellationToken ct = default) => Task.CompletedTask;
        public Task RetryAsync(long id, string reason, TimeSpan? delay = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task RequeueAsync(long id, string reason, TimeSpan? delay = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task KillAsync(long id, string reason, CancellationToken ct = default) => Task.CompletedTask;
        public Task<int> OpenCountAsync(int searchJobId, CancellationToken ct = default) => Task.FromResult(1);
        public Task<bool> AnyOfKindAsync(int searchJobId, string kind, CancellationToken ct = default) => Task.FromResult(false);
        public Task<int> ReapExhaustedAsync(CancellationToken ct = default) => Task.FromResult(0);
    }

    private sealed class InMemoryR2 : IR2StorageService
    {
        public ConcurrentDictionary<string, string> Saved { get; } = new();
        public bool IsConfigured => true;

        public Task<string?> StoreJsonAsync(string json, string objectKey, R2Bucket bucket = R2Bucket.Specs, CancellationToken ct = default)
        {
            Saved[objectKey] = json;
            return Task.FromResult<string?>($"https://r2.test/{objectKey}");
        }

        public Task<R2ObjectText?> ReadTextAsync(string key, long maxBytes = 256 * 1024, R2Bucket bucket = R2Bucket.Data, CancellationToken ct = default) =>
            Task.FromResult(Saved.TryGetValue(key, out var json) ? new R2ObjectText(json, "application/json", false) : null);

        public Task<string?> StoreImageAsync(string? sourceUrl, string keyPrefix, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<R2BucketHealth> ProbeBucketAsync(R2Bucket bucket, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<R2Listing> ListObjectsAsync(string? prefix, string? continuationToken = null, int maxKeys = 200, R2Bucket bucket = R2Bucket.Data, CancellationToken ct = default) => throw new NotSupportedException();
        public string? DownloadUrl(string key, R2Bucket bucket = R2Bucket.Data, TimeSpan? expiry = null) => throw new NotSupportedException();
    }
}
