using Daleel.Agent;
using Daleel.Core.Geo;
using Daleel.Core.Models;
using Daleel.Search.Abstractions;
using Daleel.Search.Providers;
using Daleel.Web.Cloudflare;
using Daleel.Web.Data;
using Daleel.Web.Pipeline;
using Daleel.Web.Pipeline.Enrichment;
using Daleel.Web.Profiles;
using Daleel.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Daleel.Web.Tests.Pipeline;

// ─────────────────────────────────────────────────────────────────────────────────────────────
// BrandResearchHandler: the freshness gate skips ALL research (no paid call is even possible),
// stale rows are researched in independently-guarded steps that SAVE the row as each one lands,
// one failing source never blocks the next one's save, and only an all-sources failure retries.
// ─────────────────────────────────────────────────────────────────────────────────────────────
public class BrandResearchTests
{
    // ── Local fakes (same shapes as EnrichmentHandlerTests) ────────────────────────────────────

    private sealed class RecordingQueue : IEnrichmentWorkQueue
    {
        public List<EnrichmentWorkItem> Enqueued { get; } = new();

        public Task EnqueueAsync(IReadOnlyList<EnrichmentWorkItem> items, CancellationToken ct = default)
        {
            Enqueued.AddRange(items);
            return Task.CompletedTask;
        }

        public Task<bool> EnqueueFanOutAsync(
            int searchJobId, string selfKind, IReadOnlyList<EnrichmentWorkItem> children, CancellationToken ct = default)
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
        public Task<int> OpenCountAsync(int searchJobId, CancellationToken ct = default) => Task.FromResult(0);

        public Task<bool> AnyOfKindAsync(int searchJobId, string kind, CancellationToken ct = default) =>
            Task.FromResult(false);
        public Task<int> ReapExhaustedAsync(CancellationToken ct = default) => Task.FromResult(0);
    }

    private sealed class FixedResultStore : IEnrichedResultStore
    {
        public AgentAnswer? Answer { get; set; }
        public int Patches { get; private set; }

        public Task<AgentAnswer?> LoadAsync(int jobId, CancellationToken ct = default) => Task.FromResult(Answer);

        public Task<bool> PatchAsync(
            EnrichmentWorkItem item, Func<AgentAnswer, AgentAnswer?> mutate, CancellationToken ct = default)
        {
            if (Answer is null || mutate(Answer) is not { } patched)
            {
                return Task.FromResult(false);
            }

            Answer = patched;
            Patches++;
            return Task.FromResult(true);
        }
    }

    private sealed class FakeEnrichmentService : IItemEnrichmentService
    {
        public List<ProductModel>? BrandDbFill { get; set; }
        public int BrandDbReads { get; private set; }

        public Task<ItemEnrichmentResult> EnrichAsync(
            AgentService agent, ProductSearchResult products, Action<string> progress, string? searchId, CancellationToken ct) =>
            Task.FromResult(new ItemEnrichmentResult(null, Array.Empty<Daleel.Web.Events.PipelineEvent>()));

        public Task<List<ProductModel>?> FillFromBrandDatabaseUnitAsync(List<ProductModel> models, string? geo, CancellationToken ct)
        {
            BrandDbReads++;
            return Task.FromResult(BrandDbFill);
        }

        public Task<List<ProductModel>?> IdentifyViaVisionUnitAsync(List<ProductModel> models, string? geo, CancellationToken ct) =>
            Task.FromResult<List<ProductModel>?>(null);
        public Task<ProductModel?> DeepDiveItemAsync(AgentService agent, ProductModel item, CancellationToken ct) =>
            Task.FromResult<ProductModel?>(null);
        public Task<(List<ProductModel>? Models, int Priced, IReadOnlyList<string> Created, CatalogGate Gate)> AttachCatalogForDomainAsync(
            AgentService agent, List<ProductModel> models, string domain, string? storeName, string? geo, string? searchId, string? query, string? entryUrl, CancellationToken ct, bool skipVendorCatalog = false) =>
            Task.FromResult<(List<ProductModel>?, int, IReadOnlyList<string>, CatalogGate)>((null, 0, Array.Empty<string>(), CatalogGate.EmptyCrawl));
        public Task<(List<ProductModel>? Models, int Priced, IReadOnlyList<string> Created)> AttachScrapedPricesAsync(
            List<ProductModel> models, string domain, string? storeName, string? query, CancellationToken ct) =>
            Task.FromResult<(List<ProductModel>?, int, IReadOnlyList<string>)>((null, 0, Array.Empty<string>()));
        public Task<IReadOnlyList<string>> FindImageForItemAsync(AgentService agent, ProductModel item, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        public Task<List<ProductModel>?> BackfillConditionsUnitAsync(List<ProductModel> models, CancellationToken ct) =>
            Task.FromResult<List<ProductModel>?>(null);
        public IReadOnlyList<(string Domain, string? StoreName, string? EntryUrl)> SelectCatalogDomains(ProductSearchResult products) =>
            Array.Empty<(string, string?, string?)>();
        public IReadOnlyList<string> SelectBrandsForHarvest(ProductSearchResult products) => Array.Empty<string>();
    }

    /// <summary>In-memory brand rows; snapshots EVERY upsert so incremental saves are provable.</summary>
    private sealed class FakeBrandRepository : IBrandRepository
    {
        public Brand? Row { get; set; }
        public List<Brand> Upserts { get; } = new();

        public Task<Brand?> GetByNameAsync(string name, CancellationToken ct = default) =>
            Task.FromResult(Row is { } r && r.NameKey == Brand.Normalize(name) ? r : null);

        public Task<Brand?> GetByIdAsync(int id, CancellationToken ct = default) =>
            Task.FromResult<Brand?>(null);

        public Task<Brand> UpsertAsync(Brand brand, CancellationToken ct = default)
        {
            if (brand.Id == 0)
            {
                brand.Id = Row is { Id: > 0 } existing ? existing.Id : Upserts.Count + 1;
            }

            Upserts.Add(Snapshot(brand));
            Row = brand;
            return Task.FromResult(brand);
        }

        public Task<IReadOnlyList<Brand>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Brand>>(Array.Empty<Brand>());
        public Task<IReadOnlyList<Brand>> ListStaleAsync(DateTimeOffset olderThan, int max, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Brand>>(Array.Empty<Brand>());
        public Task<int> CountAsync(CancellationToken ct = default) => Task.FromResult(Row is null ? 0 : 1);

        public Task<IReadOnlyList<Brand>> SearchAsync(string? query, int skip, int take, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Brand>>(Array.Empty<Brand>());

        private static Brand Snapshot(Brand b) => new()
        {
            Id = b.Id, Name = b.Name, NameKey = b.NameKey, Website = b.Website,
            Description = b.Description, SocialLinks = b.SocialLinks.ToList(),
            PopularModels = b.PopularModels.ToList(), CountryOfOrigin = b.CountryOfOrigin,
            LastRefreshed = b.LastRefreshed
        };
    }

    private sealed class FakeBrandCatalogService : IBrandCatalogService
    {
        /// <summary>Every harvest call: the legacy name-based path logs (Url: null, Level: "global").</summary>
        public List<(string Brand, string? Url, string Level, string? Country)> Calls { get; } = new();
        public int Harvests => Calls.Count;
        public bool Throw { get; set; }

        public Task<int> HarvestAsync(string brandName, CancellationToken ct = default)
        {
            Calls.Add((brandName, null, BrandSiteLevel.Global, null));
            return Throw ? throw new InvalidOperationException("catalogue boom") : Task.FromResult(0);
        }

        public Task<int> HarvestAsync(string brandName, string siteUrl, string level, string? countryCode, CancellationToken ct = default)
        {
            Calls.Add((brandName, siteUrl, level, countryCode));
            return Throw ? throw new InvalidOperationException("catalogue boom") : Task.FromResult(0);
        }
    }

    /// <summary>In-memory site hierarchy keyed like the real unique index (BrandId, Level, CountryCode).</summary>
    private sealed class FakeBrandSiteRepository : IBrandSiteRepository
    {
        public List<BrandSite> Rows { get; } = new();
        public int Upserts { get; private set; }

        public Task<IReadOnlyList<BrandSite>> GetForBrandAsync(int brandId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<BrandSite>>(Rows.Where(s => s.BrandId == brandId).ToList());

        public Task<BrandSite> UpsertAsync(BrandSite site, CancellationToken ct = default)
        {
            Upserts++;
            var existing = Rows.FirstOrDefault(s =>
                s.BrandId == site.BrandId && s.Level == site.Level &&
                string.Equals(s.CountryCode, site.CountryCode, StringComparison.Ordinal));
            if (existing is not null)
            {
                existing.Url = site.Url;
                existing.LastRefreshed = site.LastRefreshed;
                return Task.FromResult(existing);
            }

            site.Id = Rows.Count + 1;
            Rows.Add(site);
            return Task.FromResult(site);
        }
    }

    private sealed class FakeProviderApi : IProviderApi
    {
        public Func<string, BrandProfile?>? OnGetBrand { get; set; }
        public int BrandLookups { get; private set; }

        public bool HasScraper => false;
        public bool HasEdge => false;
        public bool EdgeDrainReady => false;
        public bool HasPlaces => false;
        public bool HasSocial => false;
        public bool HasEdgeExtract => false;
        public bool HasEdgeClassify => false;
        public bool HasEdgeFilter => false;

        public Task<BrandProfile?> GetBrandAsync(string domain, CancellationToken ct = default)
        {
            BrandLookups++;
            if (OnGetBrand is null)
            {
                throw new InvalidOperationException("no brand intelligence expected in this test");
            }

            return Task.FromResult(OnGetBrand(domain));
        }

        public Task<IReadOnlyList<CatalogProduct>> ExtractCatalogAsync(string domain, int maxProducts = 0, int timeoutMs = 45_000, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CatalogProduct>>(Array.Empty<CatalogProduct>());
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
        public Task<WorkerHandle?> SubmitEdgeBrandAsync(string domain, string brandName, string? searchJobId, bool refresh, CancellationToken ct = default) =>
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

    /// <summary>The handler with its searchy seam stubbed — no concrete AgentService anywhere.</summary>
    private sealed class StubbedHandler : BrandResearchHandler
    {
        public Func<IReadOnlyList<string>, IReadOnlyList<SearchResult>>? OnSearch { get; set; }
        public int Searches { get; private set; }

        public StubbedHandler() : base(NullLogger<BrandResearchHandler>.Instance) { }

        protected internal override Task<IReadOnlyList<SearchResult>> SearchWebAsync(
            EnrichmentUnitContext ctx, IReadOnlyList<string> queries, GeoProfile geo, CancellationToken ct)
        {
            Searches++;
            if (OnSearch is null)
            {
                throw new InvalidOperationException("no web search expected in this test");
            }

            return Task.FromResult(OnSearch(queries));
        }
    }

    // ── Scaffolding ─────────────────────────────────────────────────────────────────────────────

    private sealed record Harness(
        EnrichmentUnitContext Ctx, FixedResultStore Store, FakeBrandRepository Brands,
        FakeBrandCatalogService Catalog, FakeProviderApi Providers, FakeEnrichmentService Svc,
        FakeBrandSiteRepository Sites);

    private static Harness Build(AgentAnswer answer, Brand? savedRow = null)
    {
        var store = new FixedResultStore { Answer = answer };
        var brands = new FakeBrandRepository { Row = savedRow };
        var catalog = new FakeBrandCatalogService();
        var providers = new FakeProviderApi();
        var svc = new FakeEnrichmentService();
        var sites = new FakeBrandSiteRepository();

        var services = new ServiceCollection();
        services.AddSingleton<IBrandRepository>(brands);
        services.AddSingleton<IBrandCatalogService>(catalog);
        services.AddSingleton<IProviderApi>(providers);
        services.AddSingleton<IItemEnrichmentService>(svc);
        services.AddSingleton<IBrandSiteRepository>(sites);

        var ctx = new EnrichmentUnitContext
        {
            Services = services.BuildServiceProvider(),
            Job = new SearchJob { Id = 7, UserId = "u1", Query = "espresso machines", Geo = "jordan" },
            // Default stub THROWS: tests that must not spend a paid call get that proven for free.
            Agent = () => throw new InvalidOperationException("no paid call expected in this test"),
            Results = store,
            Queue = new RecordingQueue()
        };
        return new Harness(ctx, store, brands, catalog, providers, svc, sites);
    }

    private static AgentAnswer AcmeAnswer() => new()
    {
        Products = new ProductSearchResult
        {
            Query = "espresso machines", Geo = "jordan",
            Models = new List<ProductModel>
            {
                new() { Name = "Acme Barista 9", Brand = "Acme" },
                new() { Name = "Other Thing", Brand = "Rival" }
            },
            Brands = new List<BrandInfo> { new() { Name = "Acme" } }
        }
    };

    private static EnrichmentWorkItem Unit() => new()
    {
        Id = 1, SearchJobId = 7, UserId = "u1", HistoryEntryId = 3, ResultType = "products",
        Kind = EnrichmentUnit.BrandResearch,
        Payload = EnrichmentWorkQueue.Payload(new BrandPayload("Acme")),
        Attempts = 1, MaxAttempts = 4
    };

    private static Brand FreshRow() => new()
    {
        Id = 42, Name = "Acme", NameKey = "acme",
        Website = "https://acme.com/jo", Description = "Espresso machines for the Levant.",
        LastRefreshed = DateTimeOffset.UtcNow.AddDays(-1)
    };

    private static SearchResult Hit(string url, string snippet = "") =>
        new() { Title = url, Url = url, Snippet = snippet };

    // ── (i) Freshness gate ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Fresh_row_skips_all_research_but_still_refills_the_result()
    {
        var h = Build(AcmeAnswer(), FreshRow());
        var handler = new BrandResearchHandler(NullLogger<BrandResearchHandler>.Instance);

        // The REAL handler with the throwing agent factory, a throwing brand-intelligence fake and
        // no stubbed search: reaching any research source would explode this test.
        var outcome = await handler.ExecuteAsync(Unit(), h.Ctx, default);

        outcome.Should().BeOfType<UnitOutcome.Done>();
        h.Catalog.Harvests.Should().Be(0, "fresh means NO research — the catalogue harvest included");
        h.Providers.BrandLookups.Should().Be(0);
        h.Brands.Upserts.Should().BeEmpty("nothing was learned, so nothing re-saves");

        h.Store.Patches.Should().Be(1, "the saved research must still land on THIS result");
        var products = h.Store.Answer!.Products!;
        products.Brands[0].Url.Should().Be("https://acme.com/jo");
        products.Brands[0].DbId.Should().Be(42);
        products.Brands[0].Reputation!.Summary.Should().Be("Espresso machines for the Levant.");
        products.Models[0].BrandRegionalUrl.Should().Be("https://acme.com/jo", "Acme's model gets the regional site");
        products.Models[1].BrandRegionalUrl.Should().BeNull("Rival's model must not inherit Acme's site");
    }

    // ── (ii) Stale row: steps run and save incrementally ────────────────────────────────────────

    [Fact]
    public async Task Stale_brand_researches_in_steps_and_saves_after_each_one()
    {
        var h = Build(AcmeAnswer(), savedRow: null); // never researched
        var handler = new StubbedHandler
        {
            OnSearch = queries => queries[0].Contains("facebook", StringComparison.OrdinalIgnoreCase)
                ? new[] { Hit("https://www.facebook.com/acmecoffee") }
                : new[]
                {
                    Hit("https://somemarketplace.jo/acme-deals"), // host label ≠ brand — must be skipped
                    Hit("https://www.acme.com/jo", "Acme builds espresso machines. Founded in Amman.")
                }
        };
        h.Providers.OnGetBrand = _ => new BrandProfile
        {
            Domain = "acme.com",
            Description = "ignored — the snippet already filled it",
            Socials = new Dictionary<string, string>()
        };

        var outcome = await handler.ExecuteAsync(Unit(), h.Ctx, default);

        outcome.Should().BeOfType<UnitOutcome.Done>();
        h.Brands.Upserts.Should().HaveCountGreaterThanOrEqualTo(2,
            "each step that learns something must persist IMMEDIATELY, not at the end");

        // First save: the web step's findings alone — a crash after it loses nothing.
        h.Brands.Upserts[0].Website.Should().Be("https://www.acme.com/jo");
        h.Brands.Upserts[0].Description.Should().Contain("espresso machines");
        h.Brands.Upserts[0].SocialLinks.Should().BeEmpty();

        // Later save: the social step's findings layered on top.
        h.Brands.Row!.SocialLinks.Should().ContainSingle()
            .Which.Should().Be("https://www.facebook.com/acmecoffee");

        // The discovered hierarchy: the official site doubles as global AND (it locality-qualifies
        // for Jordan: brand host + /jo) as the local storefront — and EACH recorded level gets its
        // own TTL-self-gating harvest, global before local so local wins ModelKey collisions.
        h.Sites.Rows.Should().HaveCount(2);
        h.Sites.Rows.Should().ContainSingle(s => s.Level == BrandSiteLevel.Global && s.CountryCode == null);
        h.Sites.Rows.Should().ContainSingle(s => s.Level == BrandSiteLevel.Local && s.CountryCode == "jo");
        h.Catalog.Calls.Select(c => c.Level).Should().Equal(BrandSiteLevel.Global, BrandSiteLevel.Local);

        h.Store.Patches.Should().Be(1, "the result is refilled once at the end");
        h.Store.Answer!.Products!.Brands[0].Url.Should().Be("https://www.acme.com/jo");
    }

    // ── (iii) One failing step never blocks the next one's save ─────────────────────────────────

    [Fact]
    public async Task A_throwing_step_does_not_prevent_the_next_steps_save()
    {
        var h = Build(AcmeAnswer(), savedRow: null);
        var handler = new StubbedHandler
        {
            OnSearch = queries => queries[0].Contains("facebook", StringComparison.OrdinalIgnoreCase)
                ? new[] { Hit("https://instagram.com/acme.jo") }
                : throw new InvalidOperationException("web search boom")
        };

        var outcome = await handler.ExecuteAsync(Unit(), h.Ctx, default);

        outcome.Should().BeOfType<UnitOutcome.Done>("one dead source must not fail the unit");
        h.Brands.Upserts.Should().NotBeEmpty("the social step's save must land despite the web step throwing");
        h.Brands.Row!.Website.Should().BeNull();
        h.Brands.Row.SocialLinks.Should().ContainSingle().Which.Should().Be("https://instagram.com/acme.jo");
        h.Catalog.Harvests.Should().Be(1);
    }

    // ── (iv) Every attempted source threw → Retry ───────────────────────────────────────────────

    [Fact]
    public async Task All_sources_throwing_yields_a_retry()
    {
        var h = Build(AcmeAnswer(), savedRow: null);
        h.Catalog.Throw = true;
        var handler = new StubbedHandler(); // OnSearch unset → every search throws

        var outcome = await handler.ExecuteAsync(Unit(), h.Ctx, default);

        outcome.Should().BeOfType<UnitOutcome.Retry>()
            .Which.Reason.Should().Be("all brand research sources failed");
        h.Brands.Upserts.Should().BeEmpty("nothing was learned, so nothing may be stamped as researched");
        h.Store.Patches.Should().Be(0, "a wholly-failed pass has nothing to refill");
    }

    // ── (v) Site hierarchy: global / regional / local discovery + per-level harvest ─────────────

    [Fact]
    public async Task Discovery_records_all_three_levels_and_harvests_each_one()
    {
        var h = Build(AcmeAnswer(), savedRow: null);
        h.Providers.OnGetBrand = _ => null; // intelligence finds nothing — irrelevant here
        var handler = new StubbedHandler
        {
            OnSearch = queries =>
                queries[0].Contains("facebook", StringComparison.OrdinalIgnoreCase)
                    ? new[] { Hit("https://www.facebook.com/acmecoffee") }
                    : queries[0].Contains("official site", StringComparison.OrdinalIgnoreCase)
                        ? new[] { Hit("https://www.acme.com", "Acme espresso worldwide.") }
                        : new[] // the geo-scoped local/regional discovery
                        {
                            Hit("https://acme.jo", "Acme Jordan official store"),
                            Hit("https://acme.ae", "Acme Emirates")
                        }
        };

        var outcome = await handler.ExecuteAsync(Unit(), h.Ctx, default);

        outcome.Should().BeOfType<UnitOutcome.Done>();

        // GLOBAL = the website discovery result; LOCAL = the .jo brand host from the geo-scoped
        // search; REGIONAL = the neighbouring-market ccTLD spotted in the same results.
        h.Sites.Rows.Should().HaveCount(3);
        h.Sites.Rows.Should().ContainSingle(s => s.Level == BrandSiteLevel.Global)
            .Which.Should().BeEquivalentTo(new { CountryCode = (string?)null, Url = "https://www.acme.com" });
        h.Sites.Rows.Should().ContainSingle(s => s.Level == BrandSiteLevel.Local)
            .Which.Should().BeEquivalentTo(new { CountryCode = "jo", Url = "https://acme.jo" });
        h.Sites.Rows.Should().ContainSingle(s => s.Level == BrandSiteLevel.Regional)
            .Which.Should().BeEquivalentTo(new { CountryCode = "ae", Url = "https://acme.ae" });

        // Each discovered level gets its own harvest, global→regional→local (local lands last so
        // its rows win ModelKey collisions).
        h.Catalog.Calls.Should().Equal(
            ("Acme", "https://www.acme.com", BrandSiteLevel.Global, null),
            ("Acme", "https://acme.ae", BrandSiteLevel.Regional, "ae"),
            ("Acme", "https://acme.jo", BrandSiteLevel.Local, "jo"));

        // The refill prefers the LOCAL storefront for the models' brand link; the brand card keeps
        // the global mirror.
        var products = h.Store.Answer!.Products!;
        products.Models[0].BrandRegionalUrl.Should().Be("https://acme.jo");
        products.Brands[0].Url.Should().Be("https://www.acme.com");
    }

    [Fact]
    public async Task Fresh_site_levels_skip_rediscovery_on_the_next_run()
    {
        var h = Build(AcmeAnswer(), savedRow: null);
        h.Providers.OnGetBrand = _ => null; // description stays null → the BRAND gate never trips,
                                            // so run 2 proves the per-LEVEL gates on their own
        var handler = new StubbedHandler
        {
            OnSearch = queries =>
                queries[0].Contains("facebook", StringComparison.OrdinalIgnoreCase)
                    ? new[] { Hit("https://www.facebook.com/acmecoffee") }
                    : queries[0].Contains("official site", StringComparison.OrdinalIgnoreCase)
                        ? new[] { Hit("https://www.acme.com") } // no snippet → no description
                        : new[] { Hit("https://acme.jo", "Acme Jordan official store") }
        };

        (await handler.ExecuteAsync(Unit(), h.Ctx, default)).Should().BeOfType<UnitOutcome.Done>();
        var searchesAfterFirstRun = handler.Searches;
        var upsertsAfterFirstRun = h.Sites.Upserts;
        searchesAfterFirstRun.Should().Be(3, "web + local/regional + social each searched once");
        h.Sites.Rows.Should().HaveCount(2, "global + local were discovered (no regional hit existed)");

        (await handler.ExecuteAsync(Unit(), h.Ctx, default)).Should().BeOfType<UnitOutcome.Done>();

        handler.Searches.Should().Be(searchesAfterFirstRun,
            "fresh site rows must gate re-discovery — and a MISSING regional alone never re-triggers the paid search");
        h.Sites.Upserts.Should().Be(upsertsAfterFirstRun, "nothing new was learned, so nothing re-saves");
        h.Sites.Rows.Should().HaveCount(2, "the second run updates in place — never duplicates");
    }
}
