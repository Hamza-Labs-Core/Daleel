using System.Collections.Concurrent;
using Daleel.Core.Models;
using Daleel.Core.Persistence;
using Daleel.Web.Data;
using Daleel.Web.Persistence;
using Daleel.Web.Storage;
using Daleel.Web.Tests.Data;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Daleel.Web.Tests.Persistence;

/// <summary>
/// Round-trips the entity store against a real Postgres index + an in-memory R2: the document lands in
/// R2 (source of truth), the thin index row points at it and resolves the brand FK, and the document
/// read back from R2 still carries every embedded relation ID.
/// </summary>
public sealed class SearchEntityStoreTests : IDisposable
{
    private readonly PostgresTestContext _pg = new();
    private readonly InMemoryR2 _r2 = new();

    private SearchEntityStore NewStore() => new(
        _r2,
        new EntityRecordRepository(_pg.NewContext()),
        new BrandRepository(_pg.NewContext()),
        new StoreRepository(_pg.NewContext()),
        NullLogger<SearchEntityStore>.Instance);

    [Fact]
    public async Task SaveAsync_WritesR2Document_AndIndexRow_AndResolvesBrandFk()
    {
        // A persisted brand so the index row can resolve its FK.
        var brand = await new BrandRepository(_pg.NewContext())
            .UpsertAsync(new Brand { Name = "Gree", NameKey = Brand.Normalize("Gree") });

        var doc = new EntityDocument
        {
            Id = "p_test1",
            Intent = SearchIntentType.Product,
            Name = "Gree Pular 24000",
            Brand = "Gree",
            Model = "Pular-24",
            Geo = "jordan",
            SearchId = "job_9",
            BrandId = StableId.ForBrand("Gree"),
            ProductKey = "gree pular 24",
            Specs = new Dictionary<string, string> { ["btu"] = "24000" },
            CapturedAt = DateTimeOffset.UnixEpoch
        };

        var record = await NewStore().SaveAsync(doc);

        record.Should().NotBeNull();
        record!.Id.Should().Be("p_test1");
        record.R2Key.Should().Be("entities/product/p_test1.json");
        record.R2Url.Should().NotBeNull();              // R2 write succeeded
        record.BrandId.Should().Be(brand.Id);           // FK resolved from the brand name
        record.SearchId.Should().Be("job_9");
        record.ProductKey.Should().Be("gree pular 24");

        // The document is actually in R2 under the deterministic key.
        _r2.Saved.Should().ContainKey("entities/product/p_test1.json");

        // And the Postgres index row is queryable by relation.
        var bySearch = await new EntityRecordRepository(_pg.NewContext()).ListBySearchAsync("job_9");
        bySearch.Should().ContainSingle(r => r.Id == "p_test1");
    }

    [Fact]
    public async Task GetAsync_ReadsBackSelfContainedDocumentFromR2()
    {
        var doc = new EntityDocument
        {
            Id = "pl_test2",
            Intent = SearchIntentType.Place,
            Name = "Reem Al Bawadi",
            StoreId = StableId.ForStore("Reem Al Bawadi"),
            SearchId = "job_x",
            Specs = new Dictionary<string, string> { ["hours"] = "Daily 10-23", ["mapUrl"] = "https://maps/r" },
            CapturedAt = DateTimeOffset.UnixEpoch
        };
        await NewStore().SaveAsync(doc);

        var read = await NewStore().GetAsync("pl_test2", SearchIntentType.Place);

        read.Should().NotBeNull();
        read!.Name.Should().Be("Reem Al Bawadi");
        read.Intent.Should().Be(SearchIntentType.Place);
        // Embedded relation IDs survive the R2 round-trip — the document stands on its own.
        read.StoreId.Should().Be(StableId.ForStore("Reem Al Bawadi"));
        read.SearchId.Should().Be("job_x");
        read.Specs.Should().ContainKey("hours");
    }

    [Fact]
    public async Task SaveAsync_IsUpsert_OnRepeatedSave()
    {
        EntityDocument Make(string name) => new()
        {
            Id = "p_dup", Intent = SearchIntentType.Product, Name = name,
            SearchId = "job_d", CapturedAt = DateTimeOffset.UnixEpoch
        };

        var store = NewStore();
        await store.SaveAsync(Make("First Name"));
        await NewStore().SaveAsync(Make("Second Name"));

        var count = await new EntityRecordRepository(_pg.NewContext()).CountAsync();
        count.Should().Be(1);
        var row = await new EntityRecordRepository(_pg.NewContext()).GetByIdAsync("p_dup");
        row!.Name.Should().Be("Second Name");
    }

    // ── Dedup: save-time convergence ─────────────────────────────────────────

    [Fact]
    public async Task SaveAsync_ConvergesADifferentlyWordedDuplicate_OntoTheExistingEntity()
    {
        var store = NewStore();

        // Same physical product, extracted twice with different wording → different stable ids.
        var first = new EntityDocument
        {
            Id = StableId.ForEntity(SearchIntentType.Product, null, null, "Gree AC 12000"),
            Intent = SearchIntentType.Product, Name = "Gree AC 12000", Geo = "jordan",
            CapturedAt = DateTimeOffset.UnixEpoch
        };
        var second = new EntityDocument
        {
            Id = StableId.ForEntity(SearchIntentType.Product, null, null, "AC Gree 12000 best price"),
            Intent = SearchIntentType.Product, Name = "AC Gree 12000 best price", Geo = "jordan",
            CapturedAt = DateTimeOffset.UnixEpoch.AddDays(1)
        };
        second.Id.Should().NotBe(first.Id, "the routing ids differ — that IS the duplicate bug");

        var a = await store.SaveAsync(first);
        var b = await store.SaveAsync(second);

        b!.Id.Should().Be(a!.Id, "the identity key converges the second save onto the first entity");
        var repo = new EntityRecordRepository(_pg.NewContext());
        var alias = await repo.GetByIdAsync(second.Id);
        alias!.MergedIntoId.Should().Be(a.Id, "the incoming id stays resolvable as an alias");
        (await repo.SearchAsync(null, "Product", 0, 10)).Should().ContainSingle(
            "the directory shows ONE item, not the alias");
    }

    [Fact]
    public async Task SaveAsync_KeepsDifferentProducts_Separate()
    {
        var store = NewStore();
        await store.SaveAsync(new EntityDocument
        {
            Id = "p_a", Intent = SearchIntentType.Product, Name = "Gree AC 12000", Geo = "jordan",
            CapturedAt = DateTimeOffset.UnixEpoch
        });
        await store.SaveAsync(new EntityDocument
        {
            Id = "p_b", Intent = SearchIntentType.Product, Name = "Gree AC 18000", Geo = "jordan",
            CapturedAt = DateTimeOffset.UnixEpoch
        });

        (await new EntityRecordRepository(_pg.NewContext()).SearchAsync(null, "Product", 0, 10))
            .Should().HaveCount(2, "different capacities are different products — never merged by hash");
    }

    public void Dispose() => _pg.Dispose();

    /// <summary>An in-memory R2 that records JSON writes and serves them back (only the two methods the store uses).</summary>
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
            Task.FromResult(Saved.TryGetValue(key, out var json)
                ? new R2ObjectText(json, "application/json", false)
                : null);

        public Task<string?> StoreImageAsync(string? sourceUrl, string keyPrefix, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<R2BucketHealth> ProbeBucketAsync(R2Bucket bucket, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<R2Listing> ListObjectsAsync(string? prefix, string? continuationToken = null, int maxKeys = 200, R2Bucket bucket = R2Bucket.Data, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public string? DownloadUrl(string key, R2Bucket bucket = R2Bucket.Data, TimeSpan? expiry = null) =>
            throw new NotSupportedException();
    }
}
