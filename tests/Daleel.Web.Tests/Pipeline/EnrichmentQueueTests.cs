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

    public void Dispose() => _provider.Dispose();

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

        public Task<IReadOnlyList<EnrichmentWorkItem>> ClaimAsync(int max, TimeSpan lease, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<EnrichmentWorkItem>>(Array.Empty<EnrichmentWorkItem>());
        public Task CompleteAsync(long id, CancellationToken ct = default) => Task.CompletedTask;
        public Task RetryAsync(long id, string reason, TimeSpan? delay = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task KillAsync(long id, string reason, CancellationToken ct = default) => Task.CompletedTask;
        public Task<int> OpenCountAsync(int searchJobId, CancellationToken ct = default) => Task.FromResult(0);
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
            AgentService agent, List<ProductModel> models, string domain, string? storeName, string? geo, string? searchId, string? query, CancellationToken ct)
        {
            InlineCatalogCalls++;
            return Task.FromResult<(List<ProductModel>?, int, IReadOnlyList<string>)>((null, 0, Array.Empty<string>()));
        }

        public Task<(List<ProductModel>? Models, int Priced, IReadOnlyList<string> Created)> AttachScrapedPricesAsync(
            List<ProductModel> models, string domain, string? storeName, string? query, CancellationToken ct) =>
            Task.FromResult(DrainedResult);

        public Task<List<ProductModel>?> HarvestBrandAndRefillAsync(
            AgentService agent, string brand, List<ProductModel> models, string? geo, string? searchId, CancellationToken ct) =>
            Task.FromResult<List<ProductModel>?>(null);
        public Task<string?> FindImageForItemAsync(AgentService agent, ProductModel item, CancellationToken ct) =>
            Task.FromResult<string?>(null);
        public Task<List<ProductModel>?> BackfillConditionsUnitAsync(List<ProductModel> models, CancellationToken ct) =>
            Task.FromResult<List<ProductModel>?>(null);

        public IReadOnlyList<(string Domain, string? StoreName)> SelectCatalogDomains(ProductSearchResult products) =>
            new[] { ("store-a.jo", (string?)"Store A"), ("store-b.jo", (string?)"Store B") };
        public IReadOnlyList<string> SelectBrandsForHarvest(ProductSearchResult products) => new[] { "Krups" };
    }

    private static (EnrichmentUnitContext Ctx, RecordingQueue Queue, FixedResultStore Store, FakeEnrichmentService Svc)
        Build(AgentAnswer? answer, Func<AgentService>? agent = null)
    {
        var queue = new RecordingQueue();
        var store = new FixedResultStore { Answer = answer };
        var svc = new FakeEnrichmentService();
        var services = new ServiceCollection();
        services.AddSingleton<IItemEnrichmentService>(svc);
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
            EnrichmentUnit.BrandHarvest,
            EnrichmentUnit.ImageLookup, EnrichmentUnit.Conditions, EnrichmentUnit.Reachability
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
        svc.DrainedResult = (new List<ProductModel> { new() { Name = "A" } }, 1, Array.Empty<string>());

        var outcome = await new CatalogAttachHandler().ExecuteAsync(
            Root(EnrichmentUnit.CatalogAttach, EnrichmentWorkQueue.Payload(new CatalogPayload("store-a.jo", "Store A"))),
            ctx, default);

        outcome.Should().BeOfType<UnitOutcome.Done>();
        svc.InlineCatalogCalls.Should().Be(0, "drained edge rows make the inline crawl (and its spend) unnecessary");
        store.Patches.Should().Be(1);
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
