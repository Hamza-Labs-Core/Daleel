using Daleel.Agent;
using Daleel.Core.Models;
using Daleel.Web.Conversation;
using Daleel.Web.Data;
using Daleel.Web.Events;
using Daleel.Web.Pipeline;
using Daleel.Web.Pipeline.Enrichment;
using Daleel.Web.Services;
using Daleel.Web.Tests.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Daleel.Web.Tests.Pipeline;

// ─────────────────────────────────────────────────────────────────────────────────────────────
// EnrichmentWorkQueue semantics against real Postgres: claim/lease/retry/dead are the whole
// durability story, so they are pinned here exactly.
// ─────────────────────────────────────────────────────────────────────────────────────────────
public class EnrichmentWorkQueueTests : IDisposable
{

    private readonly ServiceProvider _provider;
    private readonly EnrichmentWorkQueue _queue;

    public EnrichmentWorkQueueTests()
    {
        // Captured ONCE: the AddDbContext lambda runs per scope, so an inline call would hand every
        // scope its own fresh (empty) database.
        var connStr = PostgresTestServer.CreateFreshDatabase();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<DaleelDbContext>(o => o.UseNpgsql(connStr));
        _provider = services.BuildServiceProvider();
        using (var scope = _provider.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<DaleelDbContext>().Database.EnsureCreated();
        }

        _queue = new EnrichmentWorkQueue(_provider.GetRequiredService<IServiceScopeFactory>());
    }

    private static EnrichmentWorkItem Item(string kind = EnrichmentUnit.ItemDive, int maxAttempts = 4) => new()
    {
        SearchJobId = 1, UserId = "u1", HistoryEntryId = 1, ResultType = "products",
        Kind = kind, Payload = "{}", MaxAttempts = maxAttempts
    };

    private DaleelDbContext NewDb() => _provider.CreateScope().ServiceProvider.GetRequiredService<DaleelDbContext>();

    [Fact]
    public async Task Claim_flips_to_running_with_lease_and_hides_from_second_claim()
    {
        await _queue.EnqueueAsync(new[] { Item() });

        var first = await _queue.ClaimAsync(5, TimeSpan.FromMinutes(10));
        first.Should().HaveCount(1);
        first[0].Status.Should().Be(WorkItemStatus.Running);
        first[0].Attempts.Should().Be(1);
        first[0].LeaseUntil.Should().NotBeNull();

        (await _queue.ClaimAsync(5, TimeSpan.FromMinutes(10))).Should().BeEmpty(
            "a leased running unit must never be handed out twice");
    }

    [Fact]
    public async Task Expired_lease_is_reclaimable_and_counts_a_new_attempt()
    {
        await _queue.EnqueueAsync(new[] { Item() });
        // Zero-length lease: expires immediately — the dead-container recovery path, no reconciler.
        (await _queue.ClaimAsync(1, TimeSpan.Zero)).Should().HaveCount(1);

        var reclaimed = await _queue.ClaimAsync(1, TimeSpan.FromMinutes(10));
        reclaimed.Should().HaveCount(1, "an expired lease means the claimer died — the unit re-runs");
        reclaimed[0].Attempts.Should().Be(2);
    }

    [Fact]
    public async Task NotBefore_defers_eligibility()
    {
        var deferred = Item();
        deferred.NotBefore = DateTimeOffset.UtcNow.AddMinutes(5);
        await _queue.EnqueueAsync(new[] { deferred });

        (await _queue.ClaimAsync(5, TimeSpan.FromMinutes(10))).Should().BeEmpty(
            "deliberately-late units (image lookups, retry backoff) must wait their turn");
    }

    [Fact]
    public async Task Retry_backs_off_then_dies_visibly_when_attempts_exhaust()
    {
        await _queue.EnqueueAsync(new[] { Item(maxAttempts: 2) });

        var claimed = (await _queue.ClaimAsync(1, TimeSpan.FromMinutes(10)))[0];
        await _queue.RetryAsync(claimed.Id, "provider hiccup");

        await using (var db = NewDb())
        {
            var row = await db.EnrichmentWorkItems.SingleAsync(i => i.Id == claimed.Id);
            row.Status.Should().Be(WorkItemStatus.Pending);
            row.NotBefore.Should().BeAfter(DateTimeOffset.UtcNow, "retries back off, never hot-loop");
            row.LastError.Should().Be("provider hiccup");
            // Make it immediately claimable for the exhaustion half of the test.
            row.NotBefore = DateTimeOffset.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync();
        }

        var second = (await _queue.ClaimAsync(1, TimeSpan.FromMinutes(10)))[0];
        second.Attempts.Should().Be(2);
        await _queue.RetryAsync(second.Id, "still failing");

        await using var check = NewDb();
        var dead = await check.EnrichmentWorkItems.SingleAsync(i => i.Id == claimed.Id);
        dead.Status.Should().Be(WorkItemStatus.Dead, "exhausted retries must be VISIBLE, never silent");
        dead.LastError.Should().Be("still failing");
        dead.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Requeue_parks_without_consuming_attempts_and_never_dies()
    {
        // A billing/infra outage must NEVER drop pending work: RequeueAsync keeps the unit Pending and
        // undoes the claim-time attempt increment, so even an arbitrarily long outage never exhausts
        // MaxAttempts and Deads — "stay queued until billing/infra is resolved".
        await _queue.EnqueueAsync(new[] { Item(maxAttempts: 2) });
        long id = 0;

        for (var cycle = 0; cycle < 20; cycle++)
        {
            var claimed = await _queue.ClaimAsync(1, TimeSpan.FromMinutes(10));
            claimed.Should().ContainSingle("a parked infra unit is always re-claimable once due");
            id = claimed[0].Id;
            await _queue.RequeueAsync(id, "infra unavailable: openrouter HTTP 402 PaymentRequired");

            await using var db = NewDb();
            var row = await db.EnrichmentWorkItems.SingleAsync(i => i.Id == id);
            row.Status.Should().Be(WorkItemStatus.Pending, "an infra park never dies, no matter how long the outage");
            row.NotBefore = DateTimeOffset.UtcNow.AddSeconds(-1); // make it due for the next cycle
            await db.SaveChangesAsync();
        }

        await using var check = NewDb();
        var final = await check.EnrichmentWorkItems.SingleAsync(i => i.Id == id);
        final.Status.Should().Be(WorkItemStatus.Pending, "20 infra cycles must not exhaust a 2-attempt budget");
        final.Attempts.Should().BeLessThanOrEqualTo(1, "each claim(+1)+requeue(-1) nets zero attempts consumed");
    }

    public void Dispose() => _provider.Dispose();

    [Fact]
    public async Task Fan_out_is_idempotent_across_a_plan_replay()
    {
        // First fan-out from a Plan row: children land.
        var children = new[] { Item(kind: EnrichmentUnit.Vision), Item(kind: EnrichmentUnit.ItemDive) };
        (await _queue.EnqueueFanOutAsync(searchJobId: 1, selfKind: EnrichmentUnit.Plan, children))
            .Should().BeTrue();

        // The Plan row re-leases after a crash (its Complete was never written) and re-runs its whole
        // fan-out. The guard must refuse — duplicating the tree re-spends on every paid scrape.
        var replay = new[] { Item(kind: EnrichmentUnit.Vision), Item(kind: EnrichmentUnit.ItemDive) };
        (await _queue.EnqueueFanOutAsync(searchJobId: 1, selfKind: EnrichmentUnit.Plan, replay))
            .Should().BeFalse("a re-run Plan must not re-spawn the deep-dive tree");

        await using var db = NewDb();
        var count = await db.EnrichmentWorkItems.CountAsync(i => i.SearchJobId == 1);
        count.Should().Be(2, "exactly one fan-out's worth of children exists after the replay");
    }

    [Fact]
    public async Task Fan_out_is_scoped_per_job()
    {
        await _queue.EnqueueFanOutAsync(1, EnrichmentUnit.Plan, new[] { Item(kind: EnrichmentUnit.Vision) });

        // A DIFFERENT job's fan-out is unaffected by job 1 already having children.
        var other = new EnrichmentWorkItem
        {
            SearchJobId = 2, UserId = "u1", HistoryEntryId = 1, ResultType = "products",
            Kind = EnrichmentUnit.Vision, Payload = "{}", MaxAttempts = 4
        };
        (await _queue.EnqueueFanOutAsync(2, EnrichmentUnit.Plan, new[] { other }))
            .Should().BeTrue("the guard is per-job, not global");
    }

    [Fact]
    public async Task Lease_expired_with_attempts_exhausted_is_not_reclaimed_and_gets_reaped()
    {
        await _queue.EnqueueAsync(new[] { Item(maxAttempts: 1) });
        // Claim with a zero lease and don't write an outcome — simulates a container that crashed
        // mid-unit (Attempts is now 1 == MaxAttempts).
        var claimed = (await _queue.ClaimAsync(1, TimeSpan.Zero))[0];
        claimed.Attempts.Should().Be(1);

        // The claim query must NOT re-run it — that infinite loop is the whole bug.
        (await _queue.ClaimAsync(5, TimeSpan.FromMinutes(10))).Should().BeEmpty(
            "an exhausted, lease-expired unit must never be re-claimed");

        var reaped = await _queue.ReapExhaustedAsync();
        reaped.Should().Be(1);

        await using var db = NewDb();
        var row = await db.EnrichmentWorkItems.SingleAsync(i => i.Id == claimed.Id);
        row.Status.Should().Be(WorkItemStatus.Dead, "the crash victim surfaces as dead, not stuck running");
        row.LastError.Should().Contain("exhausted");
    }

    [Fact]
    public async Task Complete_and_kill_are_terminal()
    {
        await _queue.EnqueueAsync(new[] { Item(), Item() });
        var claimed = await _queue.ClaimAsync(2, TimeSpan.FromMinutes(10));

        await _queue.CompleteAsync(claimed[0].Id);
        await _queue.KillAsync(claimed[1].Id, "cost cap");

        await using var db = NewDb();
        (await db.EnrichmentWorkItems.SingleAsync(i => i.Id == claimed[0].Id))
            .Status.Should().Be(WorkItemStatus.Done);
        var killed = await db.EnrichmentWorkItems.SingleAsync(i => i.Id == claimed[1].Id);
        killed.Status.Should().Be(WorkItemStatus.Dead);
        killed.LastError.Should().Be("cost cap");

        (await _queue.ClaimAsync(5, TimeSpan.FromMinutes(10))).Should().BeEmpty();
    }
}

// ─────────────────────────────────────────────────────────────────────────────────────────────
// EnrichedResultStore: sequential patches COMPOSE (each mutation sees the previous one's output),
// null mutations write nothing, and every persisted patch broadcasts an Enriched repaint.
// ─────────────────────────────────────────────────────────────────────────────────────────────
public class EnrichedResultStoreTests : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly EnrichedResultStore _store;
    private readonly RecordingEnrichedBroadcaster _broadcaster = new();

    public EnrichedResultStoreTests()
    {
        var connStr = PostgresTestServer.CreateFreshDatabase(); // once — see EnrichmentWorkQueueTests
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<DaleelDbContext>(o => o.UseNpgsql(connStr));
        services.AddScoped<IConversationStore, ConversationStore>();
        services.AddScoped<ISearchHistoryRepository, SearchHistoryRepository>();
        _provider = services.BuildServiceProvider();
        using (var scope = _provider.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<DaleelDbContext>().Database.EnsureCreated();
        }

        _store = new EnrichedResultStore(
            _provider.GetRequiredService<IServiceScopeFactory>(), _broadcaster,
            new NullCacheStore(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<EnrichedResultStore>.Instance);
    }

    private async Task<EnrichmentWorkItem> SeedJobAsync()
    {
        var answer = new AgentAnswer
        {
            Question = "coffee makers",
            Products = new ProductSearchResult
            {
                Query = "coffee makers", Geo = "Jordan",
                Models = new List<ProductModel>
                {
                    new() { Name = "Maker A" },
                    new() { Name = "Maker B" }
                }
            }
        };

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DaleelDbContext>();
        var job = new SearchJob
        {
            UserId = "u1", Query = "coffee makers", Status = JobStatus.Completed,
            ResultJson = ResultSerialization.Serialize(answer), CreatedAt = DateTimeOffset.UtcNow
        };
        db.SearchJobs.Add(job);
        await db.SaveChangesAsync();
        return new EnrichmentWorkItem
        {
            SearchJobId = job.Id, UserId = "u1", HistoryEntryId = 1, ResultType = "products",
            Kind = EnrichmentUnit.ItemDive, Payload = "{}"
        };
    }

    [Fact]
    public async Task Patches_compose_and_each_broadcasts()
    {
        var item = await SeedJobAsync();

        (await _store.PatchAsync(item, answer => Patch(answer, 0, m => m with { ImageUrl = "https://img/a" })))
            .Should().BeTrue();
        (await _store.PatchAsync(item, answer => Patch(answer, 1, m => m with { ImageUrl = "https://img/b" })))
            .Should().BeTrue();

        var current = await _store.LoadAsync(item.SearchJobId);
        current!.Products!.Models[0].ImageUrl.Should().Be("https://img/a",
            "the second patch must build on the first, not clobber it");
        current.Products.Models[1].ImageUrl.Should().Be("https://img/b");
        _broadcaster.Enriched.Should().HaveCount(2);
    }

    [Fact]
    public async Task Null_mutation_writes_and_broadcasts_nothing()
    {
        var item = await SeedJobAsync();
        (await _store.PatchAsync(item, _ => null)).Should().BeFalse();
        _broadcaster.Enriched.Should().BeEmpty("no change ⇒ no repaint");
    }

    private static AgentAnswer? Patch(AgentAnswer answer, int index, Func<ProductModel, ProductModel> mutate)
    {
        if (answer.Products is not { } p)
        {
            return null;
        }

        var models = p.Models.ToList();
        models[index] = mutate(models[index]);
        return answer with { Products = p with { Models = models } };
    }

    public void Dispose() => _provider.Dispose();

    private sealed class RecordingEnrichedBroadcaster : IConversationBroadcaster
    {
        public List<(string UserId, int JobId)> Enriched { get; } = new();

        public Task ProgressAsync(string userId, int jobId, string message) => Task.CompletedTask;
        public Task CompletedAsync(string userId, int jobId, string status, string? resultJson, string? resultType, string? error) => Task.CompletedTask;
        public Task PartialAsync(string userId, int jobId, string resultJson, string resultType) => Task.CompletedTask;

        public Task EnrichedAsync(string userId, int jobId, string resultJson, string resultType)
        {
            Enriched.Add((userId, jobId));
            return Task.CompletedTask;
        }
    }

    private sealed class NullCacheStore : Daleel.Core.Caching.ICacheStore
    {
        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);
        public Task SetAsync(string key, string value, TimeSpan ttl, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
    }
}

// ─────────────────────────────────────────────────────────────────────────────────────────────
// Fan-out + drain-await behavior over fakes: the plan unit enqueues the right children, and the
// catalogue unit prefers drained edge rows / waits for them before crawling inline.
// ─────────────────────────────────────────────────────────────────────────────────────────────
public class EnrichmentHandlerTests
{
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

    /// <summary>Flag-only config double: cloudflare.execution.enabled + int fallbacks, nothing else.</summary>
    private sealed class EdgeFlagConfig : ISystemConfigService
    {
        private readonly bool _enabled;
        public EdgeFlagConfig(bool enabled) => _enabled = enabled;

        public Task<bool> GetBoolAsync(string key, bool fallback, CancellationToken ct = default) =>
            Task.FromResult(key == Daleel.Web.Cloudflare.CloudflareWorkerOptions.EnabledFlag ? _enabled : fallback);
        public Task<string?> GetAsync(string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<int> GetIntAsync(string key, int fallback, CancellationToken ct = default) => Task.FromResult(fallback);
        public Task SetAsync(string key, string value, string type = "string", CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<SystemConfig>> AllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SystemConfig>>(Array.Empty<SystemConfig>());
        public Task SeedDefaultsAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>Edge-focused provider double: records catalog submits, everything else inert.</summary>
    private sealed class FakeEdgeProviders : IProviderApi
    {
        public bool Edge { get; init; }
        public bool DrainReady { get; init; }
        public bool SubmitSucceeds { get; init; }
        public int CatalogSubmits { get; private set; }

        public bool HasEdge => Edge;
        public bool EdgeDrainReady => DrainReady;
        public bool HasScraper => false;
        public bool HasPlaces => false;
        public bool HasSocial => false;
        public bool HasEdgeExtract => false;
        public bool HasEdgeClassify => false;
        public bool HasEdgeFilter => false;

        public Task<Daleel.Web.Cloudflare.WorkerHandle?> SubmitEdgeCatalogAsync(
            string domain, string? store, string? searchJobId, int maxProducts = 0, CancellationToken ct = default)
        {
            CatalogSubmits++;
            return Task.FromResult(SubmitSucceeds
                ? new Daleel.Web.Cloudflare.WorkerHandle { JobId = "j1", ResultKey = "k1" }
                : (Daleel.Web.Cloudflare.WorkerHandle?)null);
        }

        public Task<Daleel.Web.Cloudflare.WorkerHandle?> SubmitEdgeBrandAsync(
            string domain, string brandName, string? searchJobId, bool refresh = false, CancellationToken ct = default) =>
            Task.FromResult<Daleel.Web.Cloudflare.WorkerHandle?>(null);
        public Task<IReadOnlyList<Daleel.Search.Providers.CatalogProduct>> ExtractCatalogAsync(
            string domain, int maxProducts = 0, int timeoutMs = 45_000, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Daleel.Search.Providers.CatalogProduct>>(Array.Empty<Daleel.Search.Providers.CatalogProduct>());
        public Task<Daleel.Search.Providers.BrandProfile?> GetBrandAsync(string domain, CancellationToken ct = default) =>
            Task.FromResult<Daleel.Search.Providers.BrandProfile?>(null);
        public Task<Daleel.Search.Abstractions.ScrapedPage?> ScrapePageAsync(
            string url, Daleel.Search.Abstractions.ScrapeFormat format = Daleel.Search.Abstractions.ScrapeFormat.Markdown,
            CancellationToken ct = default) =>
            Task.FromResult<Daleel.Search.Abstractions.ScrapedPage?>(null);
        public Task<IReadOnlyList<StoreLocation>> SearchPlacesAsync(
            string query, Daleel.Core.Geo.GeoPoint? near = null, double radiusMeters = 5000,
            string? languageCode = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<StoreLocation>>(Array.Empty<StoreLocation>());
        public Task<StoreLocation?> GetPlaceDetailsAsync(string placeId, CancellationToken ct = default) =>
            Task.FromResult<StoreLocation?>(null);
        public Task<IReadOnlyList<SocialPost>> FetchSocialPostsAsync(
            Source source, string? keyword = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SocialPost>>(Array.Empty<SocialPost>());
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

    private sealed class FakeEnrichmentService : IItemEnrichmentService
    {
        public (List<ProductModel>? Models, int Priced, IReadOnlyList<string> Created) DrainedResult { get; set; } =
            (null, 0, Array.Empty<string>());
        public int InlineCatalogCalls { get; private set; }

        public Task<ItemEnrichmentResult> EnrichAsync(
            AgentService agent, ProductSearchResult products, Action<string> progress, string? searchId, CancellationToken ct) =>
            Task.FromResult(new ItemEnrichmentResult(null, Array.Empty<PipelineEvent>()));

        public Task<List<ProductModel>?> FillFromBrandDatabaseUnitAsync(List<ProductModel> models, string? geo, CancellationToken ct) =>
            Task.FromResult<List<ProductModel>?>(null);
        public Task<List<ProductModel>?> IdentifyViaVisionUnitAsync(List<ProductModel> models, string? geo, CancellationToken ct) =>
            Task.FromResult<List<ProductModel>?>(null);
        public Task<ProductModel?> DeepDiveItemAsync(AgentService agent, ProductModel item, CancellationToken ct) =>
            Task.FromResult<ProductModel?>(null);

        public Task<(List<ProductModel>? Models, int Priced, IReadOnlyList<string> Created)> AttachCatalogForDomainAsync(
            AgentService agent, List<ProductModel> models, string domain, string? storeName, string? geo, string? searchId, string? query, string? entryUrl, CancellationToken ct)
        {
            InlineCatalogCalls++;
            return Task.FromResult<(List<ProductModel>?, int, IReadOnlyList<string>)>((null, 0, Array.Empty<string>()));
        }

        public Task<(List<ProductModel>? Models, int Priced, IReadOnlyList<string> Created)> AttachScrapedPricesAsync(
            List<ProductModel> models, string domain, string? storeName, string? query, CancellationToken ct) =>
            Task.FromResult(DrainedResult);

        public Task<string?> FindImageForItemAsync(AgentService agent, ProductModel item, CancellationToken ct) =>
            Task.FromResult<string?>(null);
        public Task<List<ProductModel>?> BackfillConditionsUnitAsync(List<ProductModel> models, CancellationToken ct) =>
            Task.FromResult<List<ProductModel>?>(null);

        public IReadOnlyList<(string Domain, string? StoreName, string? EntryUrl)> SelectCatalogDomains(ProductSearchResult products) =>
            new[] { ("store-a.jo", (string?)"Store A", (string?)null), ("store-b.jo", (string?)"Store B", (string?)null) };
        public IReadOnlyList<string> SelectBrandsForHarvest(ProductSearchResult products) => new[] { "Krups" };
    }

    private static (EnrichmentUnitContext Ctx, RecordingQueue Queue, FixedResultStore Store, FakeEnrichmentService Svc)
        Build(AgentAnswer? answer, Func<AgentService>? agent = null,
            IProviderApi? providers = null, ISystemConfigService? config = null)
    {
        var queue = new RecordingQueue();
        var store = new FixedResultStore { Answer = answer };
        var svc = new FakeEnrichmentService();
        var services = new ServiceCollection();
        services.AddSingleton<IItemEnrichmentService>(svc);
        if (providers is not null)
        {
            services.AddSingleton(providers);
        }
        if (config is not null)
        {
            services.AddSingleton(config);
        }
        var ctx = new EnrichmentUnitContext
        {
            Services = services.BuildServiceProvider(),
            Job = new SearchJob { Id = 7, UserId = "u1", Query = "q" },
            // Default stub THROWS: tests that must not spend a paid call get that proven for free.
            Agent = agent ?? (() => throw new InvalidOperationException("no paid call expected in this test")),
            Results = store,
            Queue = queue
        };
        return (ctx, queue, store, svc);
    }

    private static AgentAnswer ProductAnswer(params string[] names) => new()
    {
        Products = new ProductSearchResult
        {
            Query = "q", Geo = "Jordan",
            Models = names.Select(n => new ProductModel { Name = n }).ToList()
        }
    };

    private static EnrichmentWorkItem Root(string kind, string payload = "{}", int attempts = 1) => new()
    {
        Id = 1, SearchJobId = 7, UserId = "u1", HistoryEntryId = 3, ResultType = "products",
        Kind = kind, Payload = payload, Attempts = attempts, MaxAttempts = 6
    };

    [Fact]
    public async Task Plan_fans_out_one_unit_per_item_domain_and_brand()
    {
        var (ctx, queue, _, _) = Build(ProductAnswer("A", "B", "C"));

        var outcome = await new PlanEnrichmentHandler().ExecuteAsync(Root(EnrichmentUnit.Plan), ctx, default);

        outcome.Should().BeOfType<UnitOutcome.Done>();
        queue.Enqueued.Select(i => i.Kind).Should().BeEquivalentTo(new[]
        {
            EnrichmentUnit.Vision,
            EnrichmentUnit.ItemDive, EnrichmentUnit.ItemDive, EnrichmentUnit.ItemDive,
            EnrichmentUnit.CatalogAttach, EnrichmentUnit.CatalogAttach,
            EnrichmentUnit.BrandResearch,
            EnrichmentUnit.ImageLookup, EnrichmentUnit.PriceFetch, EnrichmentUnit.Conditions,
            EnrichmentUnit.Reachability, EnrichmentUnit.ImageCheck, EnrichmentUnit.Synthesize
        });
        queue.Enqueued.Should().OnlyContain(i => i.SearchJobId == 7 && i.UserId == "u1" && i.HistoryEntryId == 3,
            "children must inherit the root's correlation so patches land on the right job");
        queue.Enqueued.Single(i => i.Kind == EnrichmentUnit.ImageLookup)
            .NotBefore.Should().BeAfter(DateTimeOffset.UtcNow, "paid image lookups run after the free sources");
    }

    [Fact]
    public async Task Catalog_prefers_drained_edge_rows_over_crawling()
    {
        var (ctx, _, store, svc) = Build(ProductAnswer("A"));
        // A genuinely priced drain result carries the offer it claims — so the additive merge has
        // something to compose (an identical model would correctly be a no-op patch).
        svc.DrainedResult = (new List<ProductModel>
        {
            new() { Name = "A", Offers = new List<PriceOffer> { new() { Source = "Edge", Url = "https://e/a", Price = 99m } } }
        }, 1, Array.Empty<string>());

        var outcome = await new CatalogAttachHandler().ExecuteAsync(
            Root(EnrichmentUnit.CatalogAttach, EnrichmentWorkQueue.Payload(new CatalogPayload("store-a.jo", "Store A"))),
            ctx, default);

        outcome.Should().BeOfType<UnitOutcome.Done>();
        svc.InlineCatalogCalls.Should().Be(0, "drained edge rows make the inline crawl (and its spend) unnecessary");
        store.Patches.Should().Be(1);
    }

    [Fact]
    public async Task Catalog_from_drain_unit_never_spends()
    {
        // A FromDrain unit exists to re-read rows the drain just persisted. When nothing matches,
        // it must simply finish: no second submit, no inline crawl — the data was already paid for.
        // No IProviderApi is registered, so even CONSULTING the edge would throw here.
        var (ctx, _, store, svc) = Build(ProductAnswer("A"));

        var outcome = await new CatalogAttachHandler().ExecuteAsync(
            Root(EnrichmentUnit.CatalogAttach,
                EnrichmentWorkQueue.Payload(new CatalogPayload("store-a.jo", "Store A", FromDrain: true))),
            ctx, default);

        outcome.Should().BeOfType<UnitOutcome.Done>();
        svc.InlineCatalogCalls.Should().Be(0);
        store.Patches.Should().Be(0);
    }

    [Fact]
    public async Task Catalog_submits_to_edge_and_finishes_without_waiting()
    {
        // Event-driven, no timers: with the edge active and nothing drained yet, the unit submits
        // (the worker dedupes re-submits) and completes — the drain enqueues the attach when the
        // crawl lands. The old behavior here was a Retry polling loop; that must never come back.
        var (ctx, _, store, svc) = Build(ProductAnswer("A"),
            providers: new FakeEdgeProviders { Edge = true, DrainReady = true, SubmitSucceeds = true },
            config: new EdgeFlagConfig(enabled: true));

        var outcome = await new CatalogAttachHandler().ExecuteAsync(
            Root(EnrichmentUnit.CatalogAttach,
                EnrichmentWorkQueue.Payload(new CatalogPayload("store-a.jo", "Store A"))),
            ctx, default);

        outcome.Should().BeOfType<UnitOutcome.Done>("the unit must finish, not retry-wait on a timer");
        ((FakeEdgeProviders)ctx.Services.GetRequiredService<IProviderApi>()).CatalogSubmits.Should().Be(1);
        svc.InlineCatalogCalls.Should().Be(0, "an accepted submit replaces the inline crawl");
        store.Patches.Should().Be(0);
    }

    [Fact]
    public async Task Catalog_falls_inline_when_the_edge_submit_fails()
    {
        // The inline fallback legitimately builds an agent; the fake service just ignores it.
        var (ctx, _, _, svc) = Build(ProductAnswer("A"), agent: () => null!,
            providers: new FakeEdgeProviders { Edge = true, DrainReady = true, SubmitSucceeds = false },
            config: new EdgeFlagConfig(enabled: true));

        var outcome = await new CatalogAttachHandler().ExecuteAsync(
            Root(EnrichmentUnit.CatalogAttach,
                EnrichmentWorkQueue.Payload(new CatalogPayload("store-a.jo", "Store A"))),
            ctx, default);

        outcome.Should().BeOfType<UnitOutcome.Done>();
        svc.InlineCatalogCalls.Should().Be(1, "an unreachable worker degrades to the inline crawl");
    }

    [Fact]
    public async Task Item_dive_relocates_by_name_when_the_index_moved()
    {
        // The dive handler legitimately builds an agent; the fake service just ignores it.
        var (ctx, _, store, _) = Build(ProductAnswer("A", "B"), agent: () => null!);

        // Payload says index 0 but names item B — the locate helper must follow the name.
        var outcome = await new ItemDiveHandler().ExecuteAsync(
            Root(EnrichmentUnit.ItemDive, EnrichmentWorkQueue.Payload(new ItemPayload(0, "B"))), ctx, default);

        // The fake service returns null (nothing to dive), so no patch — but no crash and Done.
        outcome.Should().BeOfType<UnitOutcome.Done>();
        store.Patches.Should().Be(0);
    }
}
