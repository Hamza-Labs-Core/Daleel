using System.Collections.Concurrent;
using Daleel.Web.Conversation;
using Daleel.Web.Data;
using Daleel.Web.Events;
using Daleel.Web.Tests.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Daleel.Web.Tests.Conversation;

public class ConversationBackendTests : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly RecordingBroadcaster _broadcaster = new();

    public ConversationBackendTests()
    {
        // A dedicated Postgres database on the shared test server; each DI scope opens its own
        // connection to it, the way request scopes do in production.
        var connStr = PostgresTestServer.CreateFreshDatabase();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<DaleelDbContext>(o => o.UseNpgsql(connStr));
        services.AddScoped<IConversationStore, ConversationStore>();
        services.AddScoped<IAnalyticsService>(sp => new AnalyticsService(sp.GetRequiredService<DaleelDbContext>()));
        services.AddScoped<ISearchHistoryRepository, SearchHistoryRepository>();
        services.AddScoped<IQuotaService>(sp => new QuotaService(sp.GetRequiredService<DaleelDbContext>()));
        services.AddScoped<ISearchRunner, FakeRunner>();
        // The worker records lifecycle events to the unified timeline; the no-op log keeps tests telemetry-free
        // (the same fallback production uses when Postgres isn't configured).
        services.AddSingleton<ISystemEventLog, NullSystemEventLog>();
        _provider = services.BuildServiceProvider();

        using var scope = _provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<DaleelDbContext>().Database.EnsureCreated();
        FakeRunner.CacheQuality = null; // static — reset so a prior test's cache-serve doesn't leak in
    }

    private DaleelDbContext NewDb() => _provider.CreateScope().ServiceProvider.GetRequiredService<DaleelDbContext>();

    private SearchJobService Worker() => new(
        _provider.GetRequiredService<IServiceScopeFactory>(), _broadcaster,
        NullLogger<SearchJobService>.Instance,
        new Daleel.Web.Pipeline.Enrichment.EnrichmentWorkQueue(_provider.GetRequiredService<IServiceScopeFactory>()));

    private async Task<int> SeedJobAsync(string userId = "u1")
    {
        await using var db = NewDb();
        var job = new SearchJob { UserId = userId, Query = "best AC", Status = JobStatus.Queued, CreatedAt = DateTimeOffset.UtcNow };
        db.SearchJobs.Add(job);
        await db.SaveChangesAsync();
        return job.Id;
    }

    [Fact]
    public async Task Worker_ProcessesJob_ToCompletion_AndPersistsConversation()
    {
        var jobId = await SeedJobAsync();

        await Worker().ProcessAsync(jobId, CancellationToken.None);

        await using var db = NewDb();
        var job = await db.SearchJobs.FindAsync(jobId);
        job!.Status.Should().Be(JobStatus.Completed);
        job.ResultJson.Should().Contain("FAKE-RESULT");

        var convo = await db.UserConversations.FindAsync("u1");
        convo!.CurrentStatus.Should().Be("completed");
        convo.CurrentResultType.Should().Be("ask");

        // History + analytics recorded; all of the user's devices were notified.
        (await db.SearchHistory.CountAsync()).Should().Be(1);
        (await db.AnalyticsEvents.CountAsync(e => e.EventType == "search")).Should().Be(1);
        _broadcaster.Completed.Should().ContainSingle(c => c.UserId == "u1" && c.Status == "completed");
        _broadcaster.Progress.Should().Contain(p => p.UserId == "u1");
    }

    [Fact]
    public async Task Worker_CacheServe_EnqueuesImageCheck_ToScreenServedImages()
    {
        // A cache HIT does no gather, so this run's gather-stage vision moderation never sees the served
        // images — and a replayed result can predate the image-check unit entirely (how immodest images
        // survived on a cached "women dress" result). So a cache serve must STILL enqueue an image-check
        // to screen the final grid images. Regression guard for that gap.
        FakeRunner.CacheQuality = Daleel.Web.Pipeline.CacheQualityReport.Complete; // ServeAsIs
        var jobId = await SeedJobAsync();

        await Worker().ProcessAsync(jobId, CancellationToken.None);

        await using var db = NewDb();
        var kinds = await db.EnrichmentWorkItems
            .Where(e => e.SearchJobId == jobId).Select(e => e.Kind).ToListAsync();
        kinds.Should().Contain(Daleel.Web.Pipeline.Enrichment.EnrichmentUnit.ImageCheck,
            "a served-from-cache result must still have its final images vision-screened");
    }

    [Fact]
    public async Task Worker_Polls_ClaimsAndCompletes_QueuedJob()
    {
        // Proves the Postgres-polling dispatch: the worker's background loop finds a row that was simply
        // inserted with Status=queued (no in-memory enqueue), atomically claims it, and runs it to done.
        FakeRunner.BlockUntilCancelled = false;
        var jobId = await SeedJobAsync("poll-user");

        var worker = Worker();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await worker.StartAsync(cts.Token);
        try
        {
            string? status = null;
            while (!cts.IsCancellationRequested && status != JobStatus.Completed)
            {
                await using var db = NewDb();
                status = (await db.SearchJobs.FindAsync(jobId))!.Status;
                if (status != JobStatus.Completed)
                {
                    await Task.Delay(100, cts.Token);
                }
            }

            status.Should().Be(JobStatus.Completed);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Worker_Cancellation_MarksJobCancelled()
    {
        var jobId = await SeedJobAsync();
        // All FakeRunner instances share the same static gate so the test can coordinate timing: the runner
        // signals Started, then blocks until the test releases it.
        FakeRunner.BlockUntilCancelled = true;
        FakeRunner.Started = new TaskCompletionSource();
        FakeRunner.Release = new TaskCompletionSource();

        var processing = Worker().ProcessAsync(jobId, CancellationToken.None);

        await FakeRunner.Started.Task; // the run is in flight
        // Cancel the durable way — the only way now that there's no in-memory CTS: flip the source-of-truth
        // flag, then let the run finish. The worker's pre-commit re-check sees the flag and discards the
        // result, finalizing the job as cancelled.
        await using (var db = NewDb())
        {
            var job = await db.SearchJobs.FindAsync(jobId);
            job!.CancelRequested = true;
            await db.SaveChangesAsync();
        }
        FakeRunner.Release.TrySetResult();
        await processing;

        await using var check = NewDb();
        (await check.SearchJobs.FindAsync(jobId))!.Status.Should().Be(JobStatus.Cancelled);

        FakeRunner.BlockUntilCancelled = false;
    }

    [Fact]
    public async Task ConversationService_OverQuota_Returns429_AndDoesNotEnqueue()
    {
        // Exhaust the Basic (500-credit) monthly allowance for the user.
        using (var scope = _provider.CreateScope())
        {
            var quota = scope.ServiceProvider.GetRequiredService<IQuotaService>();
            await quota.ChargeCreditsAsync("heavy", 500);
        }

        await using var db = NewDb();
        var svc = new ConversationService(db, new QuotaService(db));
        var result = await svc.SubmitAsync("heavy", isAdmin: false, "another search", "jordan", null);

        result.Accepted.Should().BeFalse();
        result.StatusCode.Should().Be(429);
        (await db.SearchJobs.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ConversationService_Submit_CreatesQueuedJob()
    {
        await using var db = NewDb();
        var svc = new ConversationService(db, new QuotaService(db));

        var result = await svc.SubmitAsync("u2", isAdmin: false, "best fridge", "jordan", "m");

        result.Accepted.Should().BeTrue();
        result.JobId.Should().NotBeNull();
        result.StatusCode.Should().Be(202);
        (await db.SearchJobs.FindAsync(result.JobId!.Value))!.Status.Should().Be(JobStatus.Queued);
    }

    [Fact]
    public async Task Reconcile_FailsOrphanedRunningJobs_AndInterruptsTheirConversations()
    {
        // A zombie left behind by a container restart: a job stuck "running" with the user's conversation
        // still spinning, plus unrelated rows that must be left untouched.
        await using (var seed = NewDb())
        {
            seed.SearchJobs.Add(new SearchJob { Id = 0, UserId = "u1", Query = "best AC", Status = JobStatus.Running, CreatedAt = DateTimeOffset.UtcNow, StartedAt = DateTimeOffset.UtcNow });
            seed.SearchJobs.Add(new SearchJob { UserId = "u2", Query = "done", Status = JobStatus.Completed, CreatedAt = DateTimeOffset.UtcNow });
            seed.SearchJobs.Add(new SearchJob { UserId = "u3", Query = "queued", Status = JobStatus.Queued, CreatedAt = DateTimeOffset.UtcNow });
            seed.UserConversations.Add(new UserConversation { UserId = "u1", CurrentStatus = "running", CurrentQuery = "best AC", StartedAt = DateTimeOffset.UtcNow });
            seed.UserConversations.Add(new UserConversation { UserId = "u2", CurrentStatus = "completed" });
            await seed.SaveChangesAsync();
        }

        await using (var db = NewDb())
        {
            await OrphanedJobReconciler.ReconcileAsync(db, NullLogger.Instance);
        }

        await using var check = NewDb();
        var jobs = await check.SearchJobs.ToListAsync();
        var orphan = jobs.Single(j => j.UserId == "u1");
        orphan.Status.Should().Be(JobStatus.Failed);
        orphan.Error.Should().Be(OrphanedJobReconciler.OrphanReason);
        orphan.CompletedAt.Should().NotBeNull();
        // Terminal and queued jobs are untouched.
        jobs.Single(j => j.UserId == "u2").Status.Should().Be(JobStatus.Completed);
        jobs.Single(j => j.UserId == "u3").Status.Should().Be(JobStatus.Queued);

        var convos = await check.UserConversations.ToListAsync();
        convos.Single(c => c.UserId == "u1").CurrentStatus.Should().Be(OrphanedJobReconciler.InterruptedStatus);
        convos.Single(c => c.UserId == "u2").CurrentStatus.Should().Be("completed");
    }

    [Fact]
    public async Task Reconcile_CleanBoot_IsNoOp()
    {
        await using (var seed = NewDb())
        {
            seed.SearchJobs.Add(new SearchJob { UserId = "u1", Query = "done", Status = JobStatus.Completed, CreatedAt = DateTimeOffset.UtcNow });
            await seed.SaveChangesAsync();
        }

        await using var db = NewDb();
        await OrphanedJobReconciler.ReconcileAsync(db, NullLogger.Instance);

        (await db.SearchJobs.SingleAsync()).Status.Should().Be(JobStatus.Completed);
    }

    private JobReconciliationService Reconciler() => new(
        _provider.GetRequiredService<IServiceScopeFactory>(), _broadcaster,
        NullLogger<JobReconciliationService>.Instance);

    [Fact]
    public async Task Cancel_QueuedJob_SetsFlagAndMarksCancelled()
    {
        var jobId = await SeedJobAsync("u1"); // seeded as Queued, never registered as running

        await using (var db = NewDb())
        {
            var ok = await new ConversationService(db, new QuotaService(db)).CancelAsync("u1", jobId);
            ok.Should().BeTrue();
        }

        await using var check = NewDb();
        var job = await check.SearchJobs.FindAsync(jobId);
        job!.CancelRequested.Should().BeTrue();           // durable source of truth set
        job.Status.Should().Be(JobStatus.Cancelled);      // queued ⇒ marked cancelled outright
        job.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Sweep_ForceCancels_CancelRequestedRunningJob()
    {
        var now = DateTimeOffset.UtcNow;
        int jobId;
        await using (var seed = NewDb())
        {
            var job = new SearchJob { UserId = "u1", Query = "x", Status = JobStatus.Running, CancelRequested = true, CreatedAt = now, StartedAt = now };
            seed.SearchJobs.Add(job);
            await seed.SaveChangesAsync();
            jobId = job.Id;
            // The conversation must reference the REAL job id (as SetRunningAsync does in production):
            // terminal conversation writes are job-scoped now, so a mismatched id is a deliberate no-op.
            seed.UserConversations.Add(new UserConversation { UserId = "u1", CurrentStatus = "running", CurrentJobId = jobId, StartedAt = now });
            await seed.SaveChangesAsync();
        }

        await Reconciler().SweepAsync(CancellationToken.None);

        await using var check = NewDb();
        var swept = await check.SearchJobs.FindAsync(jobId);
        swept!.Status.Should().Be(JobStatus.Cancelled);
        swept.CompletedAt.Should().NotBeNull();
        (await check.UserConversations.FindAsync("u1"))!.CurrentStatus.Should().Be("cancelled");
        _broadcaster.Completed.Should().Contain(c => c.JobId == jobId && c.Status == JobStatus.Cancelled);
    }

    [Fact]
    public async Task Sweep_Fails_HungRunningJob_ButLeavesYoungOneAlone()
    {
        var now = DateTimeOffset.UtcNow;
        int hungId, youngId;
        await using (var seed = NewDb())
        {
            var hung = new SearchJob { UserId = "u1", Query = "old", Status = JobStatus.Running, StartedAt = now - TimeSpan.FromMinutes(20), CreatedAt = now - TimeSpan.FromMinutes(20) };
            var young = new SearchJob { UserId = "u2", Query = "new", Status = JobStatus.Running, StartedAt = now - TimeSpan.FromMinutes(2), CreatedAt = now - TimeSpan.FromMinutes(2) };
            seed.SearchJobs.AddRange(hung, young);
            await seed.SaveChangesAsync();
            hungId = hung.Id;
            youngId = young.Id;
        }

        await Reconciler().SweepAsync(CancellationToken.None);

        await using var check = NewDb();
        var hungJob = await check.SearchJobs.FindAsync(hungId);
        hungJob!.Status.Should().Be(JobStatus.Failed);
        hungJob.Error.Should().Contain("time limit");
        // A healthy in-flight job under the 12-minute threshold is untouched.
        (await check.SearchJobs.FindAsync(youngId))!.Status.Should().Be(JobStatus.Running);
    }

    public void Dispose()
    {
        _provider.Dispose();
    }

    /// <summary>Fake runner: returns a canned result, or blocks until cancelled for the cancel test.</summary>
    private sealed class FakeRunner : ISearchRunner
    {
        public static bool BlockUntilCancelled;
        public static TaskCompletionSource? Started;
        public static TaskCompletionSource? Release;
        /// <summary>When set, the run reports a cache HIT with this verdict (else a fresh run).</summary>
        public static Daleel.Web.Pipeline.CacheQualityReport? CacheQuality;

        public async Task<SearchRunResult> RunAsync(SearchJob job, Action<string> progress, CancellationToken ct)
        {
            progress("🌐 searching…");
            if (BlockUntilCancelled)
            {
                Started?.TrySetResult();
                // Block until the test releases us, mimicking a workflow that runs to completion while a
                // cancel is being requested out-of-band (the worker's pre-commit re-check is what cancels).
                if (Release is not null)
                {
                    await Release.Task;
                }
            }

            return new SearchRunResult("{\"Summary\":\"FAKE-RESULT\"}", "ask", 0, string.Empty)
            {
                CacheQuality = CacheQuality
            };
        }
    }

    private sealed class RecordingBroadcaster : IConversationBroadcaster
    {
        public ConcurrentBag<(string UserId, int JobId, string Message)> Progress { get; } = new();
        public ConcurrentBag<(string UserId, int JobId, string Status)> Completed { get; } = new();
        public ConcurrentBag<(string UserId, int JobId, string ResultJson)> Enriched { get; } = new();
        public ConcurrentBag<(string UserId, int JobId, string ResultJson)> Partial { get; } = new();

        public Task ProgressAsync(string userId, int jobId, string message)
        {
            Progress.Add((userId, jobId, message));
            return Task.CompletedTask;
        }

        public Task CompletedAsync(string userId, int jobId, string status, string? resultJson, string? resultType, string? error)
        {
            Completed.Add((userId, jobId, status));
            return Task.CompletedTask;
        }

        public Task EnrichedAsync(string userId, int jobId, string resultJson, string resultType)
        {
            Enriched.Add((userId, jobId, resultJson));
            return Task.CompletedTask;
        }

        public Task PartialAsync(string userId, int jobId, string resultJson, string resultType)
        {
            Partial.Add((userId, jobId, resultJson));
            return Task.CompletedTask;
        }
    }
}
