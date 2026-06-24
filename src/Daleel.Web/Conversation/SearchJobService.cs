using System.Diagnostics;
using Daleel.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Daleel.Web.Conversation;

/// <summary>
/// The async backend worker. Consumes queued <see cref="SearchJob"/>s, runs the agent off the HTTP
/// thread, streams progress to the user's devices over SignalR, and persists the result + the user's
/// active conversation. Supports cooperative cancellation.
/// </summary>
public sealed class SearchJobService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ISearchJobQueue _queue;
    private readonly IConversationBroadcaster _broadcaster;
    private readonly ILogger<SearchJobService> _logger;

    public SearchJobService(
        IServiceScopeFactory scopeFactory,
        ISearchJobQueue queue,
        IConversationBroadcaster broadcaster,
        ILogger<SearchJobService> logger)
    {
        _scopeFactory = scopeFactory;
        _queue = queue;
        _broadcaster = broadcaster;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var jobId in _queue.ReadAllAsync(stoppingToken))
        {
            // One job's failure must never take down the worker loop.
            try
            {
                await ProcessAsync(jobId, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Search job {JobId} crashed the processor", jobId);
            }
        }
    }

    /// <summary>Processes a single job. Public so tests can drive it deterministically.</summary>
    public async Task ProcessAsync(int jobId, CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DaleelDbContext>();
        var runner = scope.ServiceProvider.GetRequiredService<ISearchRunner>();
        var convos = scope.ServiceProvider.GetRequiredService<IConversationStore>();
        var analytics = scope.ServiceProvider.GetRequiredService<IAnalyticsService>();
        var history = scope.ServiceProvider.GetRequiredService<ISearchHistoryRepository>();

        var job = await db.SearchJobs.FirstOrDefaultAsync(j => j.Id == jobId, stoppingToken);
        if (job is null || job.Status is JobStatus.Cancelled or JobStatus.Completed)
        {
            return;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        _queue.Register(job.Id, cts);

        var now = DateTimeOffset.UtcNow;
        job.Status = JobStatus.Running;
        job.StartedAt = now;
        job.ProgressMessage = "🔍 Generating search strategy…";
        await db.SaveChangesAsync(stoppingToken);
        await convos.SetRunningAsync(job.UserId, job.Id, job.Query, now, stoppingToken);
        await _broadcaster.ProgressAsync(job.UserId, job.Id, job.ProgressMessage);

        var sw = Stopwatch.StartNew();
        try
        {
            // Stream each agent log line to all devices (fire-and-forget; don't touch DbContext here).
            void Progress(string message) => _ = _broadcaster.ProgressAsync(job.UserId, job.Id, message);

            var result = await runner.RunAsync(job, Progress, cts.Token);
            sw.Stop();

            job.Status = JobStatus.Completed;
            job.ResultJson = result.ResultJson;
            job.ProgressMessage = "✅ Report ready!";
            job.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(stoppingToken);

            await convos.CompleteAsync(job.UserId, "completed", result.ResultJson, result.ResultType, job.CompletedAt.Value, stoppingToken);
            await history.AddAsync(new SearchHistoryEntry
            {
                UserId = job.UserId, Query = job.Query, QueryType = job.QueryType,
                Geo = job.Geo, Model = job.Model, ResultSummary = null, CreatedAt = job.CompletedAt.Value
            }, stoppingToken);
            // Record the search for analytics / cost optimisation — providers called, api-call
            // count, result count and timing. This is server-side telemetry, never shown to the user.
            await analytics.RecordSearchAsync(new AnalyticsEvent
            {
                // Anonymous analytics: a one-way hash of the user id, never the id itself.
                UserId = Anonymizer.HashUserId(job.UserId), Query = job.Query, QueryType = job.QueryType, Geo = job.Geo,
                Model = job.Model, DurationMs = (int)sw.ElapsedMilliseconds,
                ResultCount = result.ResultCount, ApiCallsMade = result.ApiCalls,
                Provider = string.IsNullOrWhiteSpace(result.Providers) ? null : result.Providers,
                FilteredCount = result.FilteredCount, FilteredCategories = result.FilteredCategories
            }, stoppingToken);

            await _broadcaster.CompletedAsync(job.UserId, job.Id, "completed", result.ResultJson, result.ResultType, null);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
        {
            await FinishAsync(db, convos, job, JobStatus.Cancelled, "cancelled", error: null, stoppingToken);
            await _broadcaster.CompletedAsync(job.UserId, job.Id, "cancelled", null, null, "Search cancelled.");
        }
        catch (Exception ex)
        {
            // Log the full exception server-side; show the user a generic message so provider/LLM/DB
            // detail (hostnames, partial keys in URLs, SQL text) never reaches the browser. The raw
            // message is kept in the job row (server-only) for operator diagnostics.
            _logger.LogError(ex, "Search job {JobId} failed", job.Id);
            await FinishAsync(db, convos, job, JobStatus.Failed, "error", ex.Message, stoppingToken);
            await _broadcaster.CompletedAsync(job.UserId, job.Id, "failed", null, null,
                "Search failed. Please try again.");
        }
        finally
        {
            _queue.Unregister(job.Id);
        }
    }

    private static async Task FinishAsync(
        DaleelDbContext db, IConversationStore convos, SearchJob job,
        string jobStatus, string convoStatus, string? error, CancellationToken ct)
    {
        job.Status = jobStatus;
        job.Error = error;
        job.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await convos.CompleteAsync(job.UserId, convoStatus, null, null, job.CompletedAt.Value, ct);
    }
}
