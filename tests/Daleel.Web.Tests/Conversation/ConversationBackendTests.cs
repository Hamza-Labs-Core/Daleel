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
        services.AddSingleton<ISearchJobQueue, SearchJobQueue>();
        // The worker records lifecycle events to the unified timeline; the no-op log keeps tests telemetry-free
        // (the same fallback production uses when Postgres isn't configured).
        services.AddSingleton<ISystemEventLog, NullSystemEventLog>();
        _provider = services.BuildServiceProvider();

        using var scope = _provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<DaleelDbContext>().Database.EnsureCreated();
    }

    private DaleelDbContext NewDb() => _provider.CreateScope().ServiceProvider.GetRequiredService<DaleelDbContext>();
    private ISearchJobQueue Queue => _provider.GetRequiredService<ISearchJobQueue>();

    private SearchJobService Worker() => new(
        _provider.GetRequiredService<IServiceScopeFactory>(), Queue, _broadcaster,
        NullLogger<SearchJobService>.Instance, new ConfigurationBuilder().Build());

    private async Task<int> SeedJobAsync(string userId = "u1")
    {
        await using var db = NewDb();
        var job = new SearchJob { UserId = userId, Query = "best AC", Status = JobStatus.Queued, CreatedAt = DateTimeOffset.UtcNow };
        db.SearchJobs.Add(job);
        await db.SaveChangesAsync();
        return job.Id;
    }

    [Fact]
    public async Task Queue_EnqueueAndRead_Works()
    {
        await Queue.EnqueueAsync(7);
        await Queue.EnqueueAsync(8);

        using var cts = new CancellationTokenSource();
        var read = new List<int>();
        await foreach (var id in Queue.ReadAllAsync(cts.Token))
        {
            read.Add(id);
            if (read.Count == 2) { cts.Cancel(); break; }
        }

        read.Should().Equal(7, 8);
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
    public async Task Worker_Cancellation_MarksJobCancelled()
    {
        var jobId = await SeedJobAsync();
        var runner = (FakeRunner)_provider.GetRequiredService<IServiceScopeFactory>()
            .CreateScope().ServiceProvider.GetRequiredService<ISearchRunner>();
        // All FakeRunner instances share the same static gate so the test can coordinate timing.
        FakeRunner.BlockUntilCancelled = true;
        FakeRunner.Started = new TaskCompletionSource();

        var processing = Worker().ProcessAsync(jobId, CancellationToken.None);

        await FakeRunner.Started.Task; // runner has begun and the job's CTS is registered
        Queue.RequestCancel(jobId).Should().BeTrue();
        await processing;

        await using var db = NewDb();
        (await db.SearchJobs.FindAsync(jobId))!.Status.Should().Be(JobStatus.Cancelled);

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
        var svc = new ConversationService(db, new QuotaService(db), Queue);
        var result = await svc.SubmitAsync("heavy", isAdmin: false, "another search", "jordan", null);

        result.Accepted.Should().BeFalse();
        result.StatusCode.Should().Be(429);
        (await db.SearchJobs.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ConversationService_Submit_CreatesQueuedJob()
    {
        await using var db = NewDb();
        var svc = new ConversationService(db, new QuotaService(db), Queue);

        var result = await svc.SubmitAsync("u2", isAdmin: false, "best fridge", "jordan", "m");

        result.Accepted.Should().BeTrue();
        result.JobId.Should().NotBeNull();
        result.StatusCode.Should().Be(202);
        (await db.SearchJobs.FindAsync(result.JobId!.Value))!.Status.Should().Be(JobStatus.Queued);
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

        public async Task<SearchRunResult> RunAsync(SearchJob job, Action<string> progress, CancellationToken ct)
        {
            progress("🌐 searching…");
            if (BlockUntilCancelled)
            {
                Started?.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct); // throws when cancelled
            }

            return new SearchRunResult("{\"Summary\":\"FAKE-RESULT\"}", "ask", 0, string.Empty);
        }
    }

    private sealed class RecordingBroadcaster : IConversationBroadcaster
    {
        public ConcurrentBag<(string UserId, int JobId, string Message)> Progress { get; } = new();
        public ConcurrentBag<(string UserId, int JobId, string Status)> Completed { get; } = new();
        public ConcurrentBag<(string UserId, int JobId, string ResultJson)> Enriched { get; } = new();

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
    }
}
