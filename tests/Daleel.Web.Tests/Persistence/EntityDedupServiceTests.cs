using System.Collections.Concurrent;
using Daleel.Core.Models;
using Daleel.Core.Persistence;
using Daleel.Web.Data;
using Daleel.Web.Persistence;
using Daleel.Web.Storage;
using Daleel.Web.Tests.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Daleel.Web.Tests.Persistence;

/// <summary>
/// The dedup worker: backfills identity keys on legacy rows, merges exact-key buckets (richest row
/// survives, R2 docs union, losers become aliases), and respects dry-run (log only, no writes).
/// </summary>
public sealed class EntityDedupServiceTests : IDisposable
{
    private readonly PostgresTestContext _pg = new();
    private readonly InMemoryR2 _r2 = new();

    public void Dispose() => _pg.Dispose();

    private IServiceProvider BuildServices(
        Daleel.Web.Identification.IVisionMatcher? vision = null, string? llmVerdict = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IR2StorageService>(_r2);
        if (vision is not null)
        {
            services.AddSingleton(vision);
        }

        if (llmVerdict is not null)
        {
            services.AddSingleton<Daleel.Web.Services.IAgentFactory>(new StubAgentFactory(llmVerdict));
        }
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
        services.AddSingleton<ISystemConfigService>(new FakeConfig());
        return services.BuildServiceProvider();
    }

    private EntityDedupService NewService(IServiceProvider sp) =>
        new(sp.GetRequiredService<IServiceScopeFactory>() ?? throw new InvalidOperationException(),
            NullLogger<EntityDedupService>.Instance);

    /// <summary>Seeds two LEGACY rows (null IdentityKey — pre-dedup saves) that are the same product.</summary>
    private async Task SeedLegacyDuplicatesAsync(IServiceProvider sp)
    {
        var store = sp.GetRequiredService<ISearchEntityStore>();
        // Written via the store so the R2 docs exist; then strip the keys to simulate legacy rows.
        await store.SaveAsync(new EntityDocument
        {
            Id = "p_old1", Intent = SearchIntentType.Product, Name = "Gree AC 12000", Geo = "jordan",
            Offers = new[] { new EntityOffer { Source = "leaders.jo", Price = 300, Currency = "JOD", Url = "https://leaders.jo/a" } },
            CapturedAt = DateTimeOffset.UnixEpoch
        });
        var db0 = sp.GetRequiredService<DaleelDbContext>();
        await db0.EntityRecords.ExecuteUpdateAsync(s => s.SetProperty(r => r.IdentityKey, (string?)null));

        await store.SaveAsync(new EntityDocument
        {
            Id = "p_old2", Intent = SearchIntentType.Product, Name = "AC Gree 12000 offer", Geo = "jordan",
            ImageUrl = "https://store.jo/gree.jpg",
            Offers = new[] { new EntityOffer { Source = "darcomjo.com", Price = 290, Currency = "JOD", Url = "https://darcomjo.com/b" } },
            CapturedAt = DateTimeOffset.UnixEpoch.AddDays(1)
        });
        var db1 = sp.GetRequiredService<DaleelDbContext>();
        await db1.EntityRecords.ExecuteUpdateAsync(s => s.SetProperty(r => r.IdentityKey, (string?)null));

        (await sp.GetRequiredService<DaleelDbContext>().EntityRecords.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Pass_BackfillsKeys_MergesTheBucket_AndAliasesTheLoser()
    {
        var sp = BuildServices();
        await SeedLegacyDuplicatesAsync(sp);

        await NewService(sp).RunPassAsync(sp, dryRun: false, default);

        var db = sp.GetRequiredService<DaleelDbContext>();
        var live = await db.EntityRecords.Where(r => r.MergedIntoId == null).ToListAsync();
        var alias = await db.EntityRecords.Where(r => r.MergedIntoId != null).ToListAsync();
        live.Should().ContainSingle("the two legacy duplicates collapse to one live entity");
        alias.Should().ContainSingle().Which.MergedIntoId.Should().Be(live[0].Id);

        // The survivor's document unified both sellers' offers (one item, offers under it).
        var doc = await sp.GetRequiredService<ISearchEntityStore>().GetAsync(live[0].Id, SearchIntentType.Product);
        doc!.Offers.Select(o => o.Source).Should().BeEquivalentTo(new[] { "leaders.jo", "darcomjo.com" });

        (await db.EntityMergeLogs.CountAsync()).Should().Be(1);
        (await db.EntityMergeLogs.SingleAsync()).Evidence.Should().Be("identity-key");
    }

    [Fact]
    public async Task DryRun_LogsTheProposal_ButWritesNothing()
    {
        var sp = BuildServices();
        await SeedLegacyDuplicatesAsync(sp);

        await NewService(sp).RunPassAsync(sp, dryRun: true, default);

        var db = sp.GetRequiredService<DaleelDbContext>();
        (await db.EntityRecords.CountAsync(r => r.MergedIntoId != null)).Should().Be(0, "dry-run merges nothing");
        (await db.EntityMergeLogs.CountAsync()).Should().Be(1);
        (await db.EntityMergeLogs.SingleAsync()).DryRun.Should().BeTrue();
    }

    // ── Stage B: fuzzy judgments + umbrella ──────────────────────────────────

    private async Task SeedFuzzyPairAsync(IServiceProvider sp, string? imgA, string? imgB)
    {
        var brand = await new BrandRepository(_pg.NewContext()).UpsertAsync(new Brand { Name = "Samsung", NameKey = Brand.Normalize("Samsung") });
        var store = sp.GetRequiredService<ISearchEntityStore>();
        await store.SaveAsync(new EntityDocument
        {
            Id = "p_fz1", Intent = SearchIntentType.Product, Name = "Samsung inverter AC deluxe", Geo = "jordan",
            Brand = "Samsung", Category = "air conditioner", ImageUrl = imgA,
            Offers = new[] { new EntityOffer { Source = "leaders.jo", Price = 500, Currency = "JOD", Url = "https://leaders.jo/x" } },
            CapturedAt = DateTimeOffset.UnixEpoch
        });
        await store.SaveAsync(new EntityDocument
        {
            Id = "p_fz2", Intent = SearchIntentType.Product, Name = "سامسونج مكيف انفرتر", Geo = "jordan",
            Brand = "Samsung", Category = "air conditioner", ImageUrl = imgB,
            Offers = new[] { new EntityOffer { Source = "darcomjo.com", Price = 490, Currency = "JOD", Url = "https://darcomjo.com/y" } },
            CapturedAt = DateTimeOffset.UnixEpoch.AddDays(1)
        });
        var db = sp.GetRequiredService<DaleelDbContext>();
        (await db.EntityRecords.CountAsync(r => r.MergedIntoId == null)).Should().Be(2,
            "cross-language names share no tokens, so the hash keeps them apart — stage B's job");
    }

    [Fact]
    public async Task VisionMatch_MergesTheCrossLanguagePair()
    {
        var sp = BuildServices(vision: new StubVision(same: true, confidence: 0.95));
        await SeedFuzzyPairAsync(sp, "https://a/img1.jpg", "https://b/img2.jpg");

        await NewService(sp).RunPassAsync(sp, dryRun: false, default);

        var db = sp.GetRequiredService<DaleelDbContext>();
        (await db.EntityRecords.CountAsync(r => r.MergedIntoId == null)).Should().Be(1);
        (await db.EntityMergeLogs.Where(m => m.Evidence == "vision").CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task UnclearVerdict_GroupsUnderTheGenericUmbrella_WithMemberOffersAndImages()
    {
        // No photos → vision can't run; the LLM judge says "unclear" → umbrella, never a blind merge.
        var sp = BuildServices(llmVerdict: "unclear");
        await SeedFuzzyPairAsync(sp, imgA: null, imgB: null);

        await NewService(sp).RunPassAsync(sp, dryRun: false, default);

        var db = sp.GetRequiredService<DaleelDbContext>();
        var live = await db.EntityRecords.Where(r => r.MergedIntoId == null).ToListAsync();
        live.Should().ContainSingle();
        live[0].Name.Should().Be("Samsung air conditioner", "the umbrella carries an honest generic title");
        live[0].IdentityKey.Should().StartWith("gen:", "an umbrella must never attract specific products");

        var doc = await sp.GetRequiredService<ISearchEntityStore>().GetAsync(live[0].Id, SearchIntentType.Product);
        doc!.Offers.Should().HaveCount(2, "each member lives on as an offer under the one item");
        doc.Offers.Select(o => o.ListingName).Should().Contain(new[] { "Samsung inverter AC deluxe", "سامسونج مكيف انفرتر" });
        (await db.EntityMergeLogs.CountAsync(m => m.Evidence == "umbrella")).Should().Be(2);
    }

    [Fact]
    public async Task DifferentVerdict_IsLedgered_AndNeverReJudged()
    {
        var sp = BuildServices(llmVerdict: "different");
        await SeedFuzzyPairAsync(sp, imgA: null, imgB: null);

        var svc = NewService(sp);
        await svc.RunPassAsync(sp, dryRun: false, default);
        await svc.RunPassAsync(sp, dryRun: false, default);

        var db = sp.GetRequiredService<DaleelDbContext>();
        (await db.EntityRecords.CountAsync(r => r.MergedIntoId == null)).Should().Be(2, "different products stay apart");
        (await db.EntityMergeLogs.CountAsync()).Should().Be(1, "the negative verdict is ledgered ONCE — never re-billed");
    }

    private sealed class StubVision(bool same, double confidence) : Daleel.Web.Identification.IVisionMatcher
    {
        public bool IsConfigured => true;
        public Task<Daleel.Web.Identification.VisionMatchResult> CompareAsync(
            string a, string b, string? hint, CancellationToken ct = default) =>
            Task.FromResult(new Daleel.Web.Identification.VisionMatchResult(same, confidence, null));
    }

    private sealed class StubAgentFactory(string verdict) : Daleel.Web.Services.IAgentFactory
    {
        public bool HasLlm() => true;
        public Daleel.Web.Services.ProviderStatus Describe() => new(true, false, false, false, false);
        public Daleel.Agent.AgentService Build(Daleel.Web.Services.AgentRequest request) => throw new NotSupportedException();
        public string? Resolve(string name) => null;
        public Daleel.Core.Llm.ILlmClient? TryBuildLlm(string? model = null) => new StubLlm(verdict);
    }

    private sealed class StubLlm(string verdict) : Daleel.Core.Llm.ILlmClient
    {
        public string Provider => "stub";
        public Task<Daleel.Core.Llm.LlmResponse> CompleteAsync(
            string systemPrompt, IReadOnlyList<Daleel.Core.Llm.LlmMessage> messages, CancellationToken ct = default) =>
            Task.FromResult(new Daleel.Core.Llm.LlmResponse { Content = $$"""{"verdict":"{{verdict}}"}""" });
    }

    private sealed class FakeConfig : ISystemConfigService
    {
        public Task<string?> GetAsync(string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<int> GetIntAsync(string key, int fallback, CancellationToken ct = default) => Task.FromResult(fallback);
        public Task<bool> GetBoolAsync(string key, bool fallback, CancellationToken ct = default) =>
            Task.FromResult(key is "dedup.fuzzy_enabled" or "dedup.enabled" || fallback);
        public Task SetAsync(string key, string value, string type = "string", CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<SystemConfig>> AllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SystemConfig>>(Array.Empty<SystemConfig>());
        public Task SeedDefaultsAsync(CancellationToken ct = default) => Task.CompletedTask;
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
