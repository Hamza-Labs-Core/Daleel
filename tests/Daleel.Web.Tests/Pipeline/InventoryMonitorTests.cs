using System.Collections.Concurrent;
using Daleel.Agent;
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
/// The store inventory monitor: Shopify parsing, the sync fan-out, page upserts (identity-keyed
/// entities + presence rows), and the finalize pass's delisting flip.
/// </summary>
public sealed class InventoryMonitorTests : IDisposable
{
    private readonly PostgresTestContext _pg = new();
    private readonly InMemoryR2 _r2 = new();

    public void Dispose() => _pg.Dispose();

    // ── Shopify parsing ──────────────────────────────────────────────────────

    [Fact]
    public void Parse_ReadsProducts_PicksCheapestAvailableVariant()
    {
        const string json = """
        {"products":[{"title":"Gree AC 12000","handle":"gree-ac-12000","vendor":"Gree","product_type":"Air Conditioner",
          "variants":[{"sku":"GR12","price":"399.0","available":false},{"sku":"GR12B","price":"350.0","available":true}],
          "images":[{"src":"https://cdn/x.jpg"}]}]}
        """;

        var listings = ShopifyCatalogClient.Parse(json, "store.jo")!;

        var l = listings.Should().ContainSingle().Subject;
        l.Name.Should().Be("Gree AC 12000");
        l.Brand.Should().Be("Gree");
        l.Sku.Should().Be("GR12B", "the cheapest AVAILABLE variant represents the listing");
        l.Price.Should().Be(350.0m);
        l.Available.Should().BeTrue();
        l.Url.Should().Be("https://store.jo/products/gree-ac-12000");
        l.ImageUrl.Should().Be("https://cdn/x.jpg");
    }

    [Fact]
    public void Parse_NonShopifyPayload_IsNull_EmptyProducts_IsEmpty()
    {
        ShopifyCatalogClient.Parse("<html>bot wall</html>", "x.jo").Should().BeNull();
        ShopifyCatalogClient.Parse("""{"products":[]}""", "x.jo").Should().BeEmpty();
    }

    // ── Units ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Sync_FansOutPages_AndFinalize_WhenTheCatalogueProbes()
    {
        var sp = Build(new StubCatalog(pages: 1));
        var (job, store) = await SeedJobAsync(sp);
        var queue = new RecordingQueue();

        var outcome = await new InventorySyncHandler(NullLogger<InventorySyncHandler>.Instance)
            .ExecuteAsync(Unit(job, EnrichmentUnit.InventorySync, SyncPayload(store)), Ctx(sp, job, queue), default);

        outcome.Should().BeOfType<UnitOutcome.Done>();
        queue.Enqueued.Should().Contain(i => i.Kind == EnrichmentUnit.InventoryPage);
        queue.Enqueued.Should().ContainSingle(i => i.Kind == EnrichmentUnit.InventoryFinalize);
    }

    [Fact]
    public async Task Page_UpsertsEntities_WithTheStoreOffer_AndPresenceRows()
    {
        var sp = Build(new StubCatalog(pages: 1));
        var (job, store) = await SeedJobAsync(sp);

        var outcome = await new InventoryPageHandler(NullLogger<InventoryPageHandler>.Instance)
            .ExecuteAsync(Unit(job, EnrichmentUnit.InventoryPage,
                EnrichmentWorkQueue.Payload(new InventoryPagePayload(store.Id, "shop.jo", 1, DateTimeOffset.UtcNow))),
                Ctx(sp, job, new RecordingQueue()), default);

        outcome.Should().BeOfType<UnitOutcome.Done>();
        var db = sp.GetRequiredService<DaleelDbContext>();
        var record = (await db.EntityRecords.Where(r => r.MergedIntoId == null).ToListAsync()).Should().ContainSingle().Subject;
        var doc = await sp.GetRequiredService<ISearchEntityStore>().GetAsync(record.Id, SearchIntentType.Product);
        doc!.Offers.Should().ContainSingle(o => o.Source == "shop.jo" && o.Price == 350.0m);
        (await db.ScrapedPrices.SingleAsync()).LastSeenAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Page_Rerun_ConvergesOntoTheSameEntity_NotADuplicate()
    {
        var sp = Build(new StubCatalog(pages: 1));
        var (job, store) = await SeedJobAsync(sp);
        var handler = new InventoryPageHandler(NullLogger<InventoryPageHandler>.Instance);
        var unit = Unit(job, EnrichmentUnit.InventoryPage,
            EnrichmentWorkQueue.Payload(new InventoryPagePayload(store.Id, "shop.jo", 1, DateTimeOffset.UtcNow)));

        await handler.ExecuteAsync(unit, Ctx(sp, job, new RecordingQueue()), default);
        await handler.ExecuteAsync(unit, Ctx(sp, job, new RecordingQueue()), default);

        var db = sp.GetRequiredService<DaleelDbContext>();
        (await db.EntityRecords.CountAsync(r => r.MergedIntoId == null)).Should().Be(1);
        (await db.ScrapedPrices.CountAsync()).Should().Be(1, "presence rows key on product+store, not per run");
    }

    [Fact]
    public async Task Finalize_FlipsItemsMissingThisSync_ToUnavailable()
    {
        var sp = Build(new StubCatalog(pages: 1));
        var (job, store) = await SeedJobAsync(sp);
        var db = sp.GetRequiredService<DaleelDbContext>();

        // A previously-synced listing the store no longer carries (watermark predates this sync).
        db.ScrapedPrices.Add(new ScrapedPrice
        {
            ProductName = "Delisted fan", ProductKey = "delisted fan", StoreName = "shop.jo",
            Provider = "inventory", Availability = "in stock",
            ScrapedAt = DateTimeOffset.UtcNow.AddDays(-2), LastSeenAt = DateTimeOffset.UtcNow.AddDays(-2)
        });
        await db.SaveChangesAsync();

        var outcome = await new InventoryFinalizeHandler(NullLogger<InventoryFinalizeHandler>.Instance)
            .ExecuteAsync(Unit(job, EnrichmentUnit.InventoryFinalize, SyncPayload(store)),
                Ctx(sp, job, new RecordingQueue()), default);

        outcome.Should().BeOfType<UnitOutcome.Done>();
        var db2 = sp.GetRequiredService<DaleelDbContext>();
        (await db2.ScrapedPrices.SingleAsync(r => r.ProductKey == "delisted fan"))
            .Availability.Should().Be("unavailable", "missing across a whole sync = delisted; flipped, never deleted");
        (await db2.Stores.SingleAsync(s => s.Id == store.Id)).LastInventorySyncAt.Should().NotBeNull();
    }

    [Fact]
    public async Task ProfileRefresh_Upsert_NeverWipesTheMonitorFlag()
    {
        var sp = Build(new StubCatalog(pages: 1));
        var repo = new StoreRepository(_pg.NewContext());
        var store = await repo.UpsertAsync(new Store
        {
            Name = "Shop JO", NameKey = Store.Normalize("Shop JO"), Website = "https://shop.jo",
            LastRefreshed = DateTimeOffset.UtcNow
        });
        await new StoreRepository(_pg.NewContext()).SetMonitorAsync(store.Id, enabled: true);

        // A later profile research pass upserts a FRESH object (MonitorEnabled defaults false).
        await new StoreRepository(_pg.NewContext()).UpsertAsync(new Store
        {
            Name = "Shop JO", Website = "https://shop.jo", Type = "electronics",
            LastRefreshed = DateTimeOffset.UtcNow
        });

        var reloaded = await new StoreRepository(_pg.NewContext()).GetByIdAsync(store.Id);
        reloaded!.MonitorEnabled.Should().BeTrue("monitoring is OPERATOR state — profile refreshes must never wipe it");
    }

    // ── Harness ──────────────────────────────────────────────────────────────

    private static string SyncPayload(Store store) =>
        EnrichmentWorkQueue.Payload(new InventorySyncPayload(store.Id, "shop.jo", DateTimeOffset.UtcNow));

    private IServiceProvider Build(IStoreCatalogClient catalog)
    {
        var services = new ServiceCollection();
        services.AddSingleton(catalog);
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

    private static EnrichmentUnitContext Ctx(IServiceProvider sp, SearchJob job, IEnrichmentWorkQueue queue) => new()
    {
        Services = sp,
        Job = job,
        Agent = () => null!,
        Results = new NullResults(),
        Queue = queue
    };

    private sealed class StubCatalog(int pages) : IStoreCatalogClient
    {
        public Task<(IReadOnlyList<InventoryListing> Listings, string RawPayload)?> GetPageAsync(
            string domain, int page, CancellationToken ct = default)
        {
            if (page > pages)
            {
                return Task.FromResult<(IReadOnlyList<InventoryListing>, string)?>(
                    (Array.Empty<InventoryListing>(), """{"products":[]}"""));
            }

            IReadOnlyList<InventoryListing> listings = new[]
            {
                new InventoryListing("Gree AC 12000", "Gree", "GR12B", "Air Conditioner",
                    350.0m, true, $"https://{domain}/products/gree-ac-12000", "https://cdn/x.jpg")
            };
            return Task.FromResult<(IReadOnlyList<InventoryListing>, string)?>((listings, $$"""{"page":{{page}}}"""));
        }
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
