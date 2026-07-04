using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Daleel.Web.Cloudflare;
using Daleel.Web.Data;
using Daleel.Web.Events;
using Daleel.Web.Storage;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Daleel.Web.Tests.Cloudflare;

/// <summary>
/// Covers the Cloudflare execution layer's VPS half: the worker client (submit / status / R2 result
/// reads), the Queues pull consumer, and — most importantly — the poll-drain service that persists
/// finished edge results independent of any workflow's lifetime. Everything runs against local fakes;
/// the contracts are the worker's JSON envelopes verbatim.
/// </summary>
public class CloudflareExecutionTests
{
    private static CloudflareWorkerOptions Options(bool withQueue = false) => new()
    {
        ScrapeWorkerUrl = new Uri("https://scrape.test"),
        ScrapeWorkerToken = "token",
        AccountId = withQueue ? "acct1" : null,
        QueuesApiToken = withQueue ? "qtoken" : null,
        PollQueueId = withQueue ? "queue1" : null
    };

    // ── Options ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Options_RequireWorkerUrlAndToken()
    {
        CloudflareWorkerOptions.FromConfiguration(Config(new())).Should().BeNull(
            "an unconfigured environment must leave the app exactly as before");

        CloudflareWorkerOptions.FromConfiguration(Config(new()
        {
            ["CF_SCRAPE_WORKER_URL"] = "https://scrape.test",
            ["CF_SCRAPE_WORKER_TOKEN"] = "t"
        })).Should().NotBeNull();

        CloudflareWorkerOptions.FromConfiguration(Config(new()
        {
            ["CF_SCRAPE_WORKER_URL"] = "not a url",
            ["CF_SCRAPE_WORKER_TOKEN"] = "t"
        })).Should().BeNull("a malformed URL must not half-configure the layer");
    }

    [Fact]
    public void Options_CanDrainQueue_NeedsAllThreeQueueSettings()
    {
        Options(withQueue: false).CanDrainQueue.Should().BeFalse();
        Options(withQueue: true).CanDrainQueue.Should().BeTrue();
    }

    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    // ── Worker client ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task SubmitCatalog_ParsesAcceptEnvelope_AndOmitsCapWhenUncapped()
    {
        var handler = new StubHandler(_ => (HttpStatusCode.Accepted,
            """{ "ok": true, "mode": "async", "jobId": "j1", "resultKey": "qa/pipeline/42/catalog/j1.json" }"""));
        var client = Client(handler);

        var handle = await client.SubmitCatalogAsync("store.example", "ABC Store", "42", maxProducts: 0);

        handle.Should().NotBeNull();
        handle!.JobId.Should().Be("j1");
        handle.ResultKey.Should().Be("qa/pipeline/42/catalog/j1.json");

        handler.Requests.Should().ContainSingle();
        handler.Requests[0].RequestUri!.AbsolutePath.Should().Be("/scrape/catalog");
        handler.Bodies[0].Should().Contain("store.example");
        // maxProducts 0 ⇒ uncapped ⇒ the field must be ABSENT so the worker applies the vendor ceiling.
        handler.Bodies[0].Should().NotContain("maxProducts\":0");
    }

    [Fact]
    public async Task SubmitCatalog_ReturnsNull_OnRejectionOrTransportFailure()
    {
        var rejecting = Client(new StubHandler(_ => (HttpStatusCode.BadRequest,
            """{ "ok": false, "error": { "code": "bad_request", "message": "domain is required", "retryable": false } }""")));
        (await rejecting.SubmitCatalogAsync("", null, null)).Should().BeNull();

        var throwing = Client(new StubHandler(_ => throw new HttpRequestException("down")));
        (await throwing.SubmitCatalogAsync("store.example", null, null)).Should().BeNull(
            "an unreachable worker must degrade to the inline path, never fault the search");
    }

    [Fact]
    public async Task ReadResult_DeserializesCatalogDoc_AndRefusesTruncated()
    {
        var doc = """
            { "kind": "catalog", "domain": "store.example", "store": "ABC Store", "searchJobId": "42",
              "capturedAt": "2026-07-04T10:00:00Z", "productCount": 1,
              "products": [ { "name": "Espresso X", "price": 139, "currency": "JOD", "url": "https://store.example/x", "sku": "EX1" } ] }
            """;
        var r2 = new FakeR2();
        r2.Objects["k.json"] = doc;
        var client = Client(new StubHandler(_ => (HttpStatusCode.OK, "{}")), r2);

        var parsed = await client.ReadResultAsync<CatalogResultDoc>("k.json");
        parsed.Should().NotBeNull();
        parsed!.Products.Should().ContainSingle(p => p.Name == "Espresso X" && p.Price == 139m && p.Sku == "EX1");

        r2.Truncate = true;
        (await client.ReadResultAsync<CatalogResultDoc>("k.json")).Should().BeNull(
            "a clipped JSON document must be treated as unreadable, not silently partial");
    }

    // ── Queue pull client ───────────────────────────────────────────────────────────

    [Fact]
    public async Task QueuePull_IsInert_WhenNotConfigured()
    {
        var handler = new StubHandler(_ => (HttpStatusCode.OK, "{}"));
        var client = new QueuePullClient(new HttpClient(handler), Options(withQueue: false),
            NullLogger<QueuePullClient>.Instance);

        (await client.PullAsync()).Should().BeEmpty();
        await client.AckAsync(new[] { "l1" }, Array.Empty<(string, int)>());
        handler.Requests.Should().BeEmpty("no credentials ⇒ no Cloudflare API calls at all");
    }

    [Fact]
    public async Task QueuePull_LeasesMessages_AndAcksWithBackoff()
    {
        var handler = new StubHandler(req => (HttpStatusCode.OK,
            req.RequestUri!.AbsolutePath.EndsWith("/pull")
                ? """{ "result": { "messages": [ { "lease_id": "l1", "body": "{\"type\":\"poll\"}", "attempts": 2 } ] } }"""
                : "{ \"success\": true }"));
        var client = new QueuePullClient(new HttpClient(handler), Options(withQueue: true),
            NullLogger<QueuePullClient>.Instance);

        var messages = await client.PullAsync(batchSize: 5);
        messages.Should().ContainSingle(m => m.LeaseId == "l1" && m.Attempts == 2);
        handler.Requests[0].RequestUri!.AbsolutePath.Should().Be("/client/v4/accounts/acct1/queues/queue1/messages/pull");

        await client.AckAsync(new[] { "l1" }, new[] { ("l2", 30) });
        handler.Requests[1].RequestUri!.AbsolutePath.Should().EndWith("/messages/ack");
        handler.Bodies[1].Should().Contain("l1").And.Contain("l2").And.Contain("\"delay_seconds\":30");
    }

    // ── Poll message parsing ────────────────────────────────────────────────────────

    [Fact]
    public void ParseBody_AcceptsDirectJson_AndBase64WrappedJson()
    {
        const string json = """{ "type": "poll", "kind": "catalog", "jobId": "j1", "resultKey": "k", "status": "done" }""";

        var direct = CloudflarePollDrainService.ParseBody(json);
        direct.Should().NotBeNull();
        direct!.Kind.Should().Be("catalog");
        direct.IsDone.Should().BeTrue();

        var base64 = CloudflarePollDrainService.ParseBody(Convert.ToBase64String(Encoding.UTF8.GetBytes(json)));
        base64.Should().NotBeNull();
        base64!.JobId.Should().Be("j1");

        CloudflarePollDrainService.ParseBody("not json at all").Should().BeNull();
        CloudflarePollDrainService.ParseBody(null).Should().BeNull();
    }

    // ── Drain service ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Drain_PersistsFinishedCatalog_MarksAndAcks()
    {
        var fixture = new DrainFixture();
        fixture.Worker.Results["qa/r.json"] = new CatalogResultDoc
        {
            Kind = "catalog", Domain = "store.example", Store = "ABC Store", SearchJobId = "42",
            CapturedAt = new DateTimeOffset(2026, 7, 4, 10, 0, 0, TimeSpan.Zero),
            ProductCount = 2,
            Products =
            {
                new() { Name = "Espresso X", Price = 139m, Currency = "JOD", Url = "https://store.example/x" },
                new() { Name = "Unpriced Y", Price = null } // price-less → not an observation
            }
        };
        fixture.Queue.Enqueue(PollJson(status: "done", resultKey: "qa/r.json", store: "ABC Store"));

        await fixture.Service.DrainOnceAsync(CancellationToken.None);

        var saved = await fixture.Prices.LatestForStoreAsync("ABC Store");
        saved.Should().ContainSingle(p => p.ProductName == "Espresso X" && p.Price == 139m,
            "the priced product is persisted; the unpriced one is skipped");
        saved[0].Provider.Should().Be("scrape-worker/context.dev");
        saved[0].ScrapedAt.Should().Be(new DateTimeOffset(2026, 7, 4, 10, 0, 0, TimeSpan.Zero),
            "the crawl's capture time wins over the drain time");

        fixture.Queue.Acked.Should().ContainSingle();
        fixture.R2.Objects.Keys.Should().Contain("qa/r.json.persisted", "the idempotency marker is written");
        fixture.Events.Published.Should().ContainSingle(e => e.EventType == "store.prices.drained");
    }

    [Fact]
    public async Task Drain_SkipsPersist_WhenMarkerAlreadyExists()
    {
        var fixture = new DrainFixture();
        fixture.R2.Objects["qa/r.json.persisted"] = "{}"; // a previous delivery already persisted
        fixture.Queue.Enqueue(PollJson(status: "done", resultKey: "qa/r.json", store: "ABC Store"));

        await fixture.Service.DrainOnceAsync(CancellationToken.None);

        (await fixture.Prices.CountAsync()).Should().Be(0, "at-least-once redelivery must not duplicate rows");
        fixture.Queue.Acked.Should().ContainSingle();
    }

    [Fact]
    public async Task Drain_Retries_WhenDoneResultIsNotYetReadable()
    {
        var fixture = new DrainFixture();
        // Worker says done, but the result key resolves to nothing (e.g. transient R2 hiccup).
        fixture.Queue.Enqueue(PollJson(status: "done", resultKey: "qa/missing.json", store: "ABC Store",
            deadlineAt: DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeMilliseconds()));

        await fixture.Service.DrainOnceAsync(CancellationToken.None);

        fixture.Queue.Acked.Should().BeEmpty();
        fixture.Queue.Retried.Should().ContainSingle("an unreadable done-result is retried, not dropped");
    }

    [Fact]
    public async Task Drain_AcksTerminalFailure_AndSurfacesItOnTheTimeline()
    {
        var fixture = new DrainFixture();
        fixture.Queue.Enqueue(PollJson(status: "error", resultKey: "qa/r.json", store: "ABC Store",
            error: "context.dev 401: bad key"));

        await fixture.Service.DrainOnceAsync(CancellationToken.None);

        fixture.Queue.Acked.Should().ContainSingle();
        fixture.Events.Published.Should().ContainSingle(e =>
            e.EventType == "store.prices.edge_failed" && e.Severity == SystemEventSeverity.Warning,
            "faulted ≠ empty: the failure must be visible, never silently swallowed");
        (await fixture.Prices.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Drain_AcksPoisonMessages_InsteadOfLoopingForever()
    {
        var fixture = new DrainFixture();
        fixture.Queue.Enqueue("!! not json !!");

        await fixture.Service.DrainOnceAsync(CancellationToken.None);

        fixture.Queue.Acked.Should().ContainSingle();
        fixture.Queue.Retried.Should().BeEmpty();
    }

    private static string PollJson(
        string status, string resultKey, string store, string? error = null, long? deadlineAt = null) =>
        JsonSerializer.Serialize(new
        {
            type = "poll",
            worker = "scrape",
            kind = "catalog",
            jobId = "j1",
            resultKey,
            searchJobId = "42",
            store,
            domain = "store.example",
            status,
            error,
            enqueuedAt = 1_700_000_000_000,
            deadlineAt = deadlineAt ?? 0
        });

    // ── Fixture & fakes ─────────────────────────────────────────────────────────────

    private static CloudflareWorkerClient Client(StubHandler handler, IR2StorageService? r2 = null) =>
        new(new HttpClient(handler), Options(), r2 ?? new FakeR2(),
            NullLogger<CloudflareWorkerClient>.Instance);

    /// <summary>Everything a drain test needs, wired the way Program.cs wires production.</summary>
    private sealed class DrainFixture
    {
        public FakeQueue Queue { get; } = new();
        public FakeWorkerClient Worker { get; } = new();
        public FakeR2 R2 { get; } = new();
        public InMemoryScrapedPriceRepo Prices { get; } = new();
        public FakeEventLog Events { get; } = new();
        public CloudflarePollDrainService Service { get; }

        public DrainFixture()
        {
            var services = new ServiceCollection();
            services.AddSingleton<IScrapedPriceRepository>(Prices);
            services.AddSingleton<ISystemEventLog>(Events);
            var provider = services.BuildServiceProvider();

            Service = new CloudflarePollDrainService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                Queue, Worker, R2, NullLogger<CloudflarePollDrainService>.Instance);
        }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, (HttpStatusCode Status, string Body)> _responder;
        public List<HttpRequestMessage> Requests { get; } = new();
        public List<string> Bodies { get; } = new();

        public StubHandler(Func<HttpRequestMessage, (HttpStatusCode, string)> responder) => _responder = responder;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            var (status, body) = _responder(request);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class FakeQueue : IQueuePullClient
    {
        private readonly Queue<PulledMessage> _pending = new();
        public List<string> Acked { get; } = new();
        public List<(string LeaseId, int DelaySeconds)> Retried { get; } = new();

        public bool IsConfigured => true;

        public void Enqueue(string body) =>
            _pending.Enqueue(new PulledMessage { LeaseId = $"lease-{_pending.Count + 1}", Body = body, Attempts = 1 });

        public Task<IReadOnlyList<PulledMessage>> PullAsync(
            int batchSize = 10, int visibilityTimeoutMs = 60_000, CancellationToken ct = default)
        {
            var batch = new List<PulledMessage>();
            while (batch.Count < batchSize && _pending.Count > 0)
            {
                batch.Add(_pending.Dequeue());
            }
            return Task.FromResult<IReadOnlyList<PulledMessage>>(batch);
        }

        public Task AckAsync(
            IReadOnlyList<string> ackLeaseIds, IReadOnlyList<(string LeaseId, int DelaySeconds)> retries,
            CancellationToken ct = default)
        {
            Acked.AddRange(ackLeaseIds);
            Retried.AddRange(retries);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeWorkerClient : ICloudflareWorkerClient
    {
        public Dictionary<string, CatalogResultDoc> Results { get; } = new();
        public List<(string Domain, string? Store, string? SearchJobId, int MaxProducts)> Submits { get; } = new();

        public Task<WorkerHandle?> SubmitCatalogAsync(
            string domain, string? store, string? searchJobId, int maxProducts = 0, CancellationToken ct = default)
        {
            Submits.Add((domain, store, searchJobId, maxProducts));
            return Task.FromResult<WorkerHandle?>(new WorkerHandle { JobId = "j1", ResultKey = $"qa/{domain}.json" });
        }

        public Task<WorkerJobStatus?> GetJobStatusAsync(string jobId, CancellationToken ct = default) =>
            Task.FromResult<WorkerJobStatus?>(new WorkerJobStatus { Ok = true, Status = "done", JobId = jobId });

        public Task<T?> ReadResultAsync<T>(string resultKey, CancellationToken ct = default) where T : class =>
            Task.FromResult(Results.TryGetValue(resultKey, out var doc) ? doc as T : null);
    }

    private sealed class FakeR2 : IR2StorageService
    {
        public ConcurrentDictionary<string, string> Objects { get; } = new();
        public bool Truncate { get; set; }

        public bool IsConfigured => true;

        public Task<R2BucketHealth> ProbeBucketAsync(R2Bucket bucket, CancellationToken ct = default) =>
            Task.FromResult(new R2BucketHealth(bucket, "fake", Reachable: true, HasObjects: true, PublicUrl: null, Error: null));

        public Task<string?> StoreImageAsync(string? sourceUrl, string keyPrefix, CancellationToken ct = default) =>
            Task.FromResult(sourceUrl);

        public Task<string?> StoreJsonAsync(
            string json, string objectKey, R2Bucket bucket = R2Bucket.Specs, CancellationToken ct = default)
        {
            Objects[objectKey] = json;
            return Task.FromResult<string?>(null);
        }

        public Task<R2Listing> ListObjectsAsync(
            string? prefix, string? continuationToken = null, int maxKeys = 200,
            R2Bucket bucket = R2Bucket.Data, CancellationToken ct = default) =>
            Task.FromResult(R2Listing.Empty);

        public Task<R2ObjectText?> ReadTextAsync(
            string key, long maxBytes = 256 * 1024, R2Bucket bucket = R2Bucket.Data, CancellationToken ct = default) =>
            Task.FromResult(Objects.TryGetValue(key, out var text)
                ? new R2ObjectText(text, "application/json", Truncate)
                : null);

        public string? DownloadUrl(string key, R2Bucket bucket = R2Bucket.Data, TimeSpan? expiry = null) => null;
    }

    private sealed class FakeEventLog : ISystemEventLog
    {
        public List<SystemEvent> Published { get; } = new();
        public bool IsEnabled => true;

        public Task PublishAsync(SystemEvent ev, CancellationToken ct = default)
        {
            Published.Add(ev);
            return Task.CompletedTask;
        }

        public Task PublishManyAsync(IReadOnlyCollection<SystemEvent> events, CancellationToken ct = default)
        {
            Published.AddRange(events);
            return Task.CompletedTask;
        }

        public Task<SystemEventPage> QueryAsync(SystemEventQuery query, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class InMemoryScrapedPriceRepo : IScrapedPriceRepository
    {
        private readonly ConcurrentBag<ScrapedPrice> _rows = new();

        public Task AddAsync(ScrapedPrice price, CancellationToken ct = default)
        {
            _rows.Add(price);
            return Task.CompletedTask;
        }

        public Task AddRangeAsync(IReadOnlyCollection<ScrapedPrice> prices, CancellationToken ct = default)
        {
            foreach (var p in prices) _rows.Add(p);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ScrapedPrice>> LatestForProductAsync(string productKey, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ScrapedPrice>>(_rows.Where(r => r.ProductKey == productKey).ToList());

        public Task<IReadOnlyList<ScrapedPrice>> LatestForStoreAsync(string storeName, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ScrapedPrice>>(
                _rows.Where(r => string.Equals(r.StoreName, storeName, StringComparison.OrdinalIgnoreCase)).ToList());

        public Task<IReadOnlyList<ScrapedPrice>> HistoryForProductAsync(string productKey, int max = 500, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ScrapedPrice>>(_rows.Where(r => r.ProductKey == productKey).Take(max).ToList());

        public Task<int> CountAsync(CancellationToken ct = default) => Task.FromResult(_rows.Count);
    }
}
