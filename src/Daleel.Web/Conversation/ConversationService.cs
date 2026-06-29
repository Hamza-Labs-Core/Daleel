using Daleel.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Daleel.Web.Conversation;

/// <summary>Outcome of submitting a search: accepted with a job id, or rejected with a reason.</summary>
public sealed record SubmitResult(bool Accepted, int? JobId, string? Error, int StatusCode);

/// <summary>
/// Front door for the async conversation flow. Enforces the quota, creates the job, and enqueues it.
/// Shared by the /api/search endpoint and the interactive conversation page so both behave the same.
/// </summary>
public interface IConversationService
{
    Task<SubmitResult> SubmitAsync(string userId, bool isAdmin, string query, string? geo, string? model, string? language = null, CancellationToken ct = default);
    Task<bool> CancelAsync(string userId, int jobId, CancellationToken ct = default);
}

public sealed class ConversationService : IConversationService
{
    private readonly DaleelDbContext _db;
    private readonly IQuotaService _quota;
    private readonly ISearchJobQueue _queue;
    private readonly ISystemConfigService? _config;

    // _config is optional so tests can construct without it; DI supplies it in the app, where it
    // provides the per-plan default model ids (model.default_free / model.default_pro).
    public ConversationService(
        DaleelDbContext db, IQuotaService quota, ISearchJobQueue queue, ISystemConfigService? config = null)
    {
        _db = db;
        _quota = quota;
        _queue = queue;
        _config = config;
    }

    public async Task<SubmitResult> SubmitAsync(
        string userId, bool isAdmin, string query, string? geo, string? model, string? language = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return new SubmitResult(false, null, "Query is required.", 400);
        }

        // Cheap pre-check: a user with no credits left can't start a search. The actual cost is
        // charged after it runs (it depends on the providers it calls).
        var status = await _quota.GetStatusAsync(userId, isAdmin, ct);
        if (!status.CanSearch)
        {
            return new SubmitResult(false, null,
                $"You're out of credits for this month ({status.Limit} included). Upgrade for more.", 429);
        }

        // When the caller didn't pick a model, fall back to the admin-configured per-plan default.
        var resolvedModel = model;
        if (string.IsNullOrWhiteSpace(resolvedModel) && _config is not null)
        {
            var key = status.PlanName == "Basic" ? "model.default_free" : "model.default_pro";
            resolvedModel = await _config.GetAsync(key, ct);
        }

        var job = new SearchJob
        {
            UserId = userId,
            Query = query.Trim(),
            QueryType = "ask",
            Geo = string.IsNullOrWhiteSpace(geo) ? "jordan" : geo,
            Model = resolvedModel ?? string.Empty,
            Language = string.IsNullOrWhiteSpace(language) ? "en" : language,
            Status = JobStatus.Queued,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _db.SearchJobs.Add(job);
        await _db.SaveChangesAsync(ct);

        // No up-front consume: credits are charged for the actual provider calls once the job finishes
        // (see SearchJobService), so the CanSearch pre-check above is the gate.
        await _queue.EnqueueAsync(job.Id, ct);
        return new SubmitResult(true, job.Id, null, 202);
    }

    public async Task<bool> CancelAsync(string userId, int jobId, CancellationToken ct = default)
    {
        var job = await _db.SearchJobs.FirstOrDefaultAsync(j => j.Id == jobId && j.UserId == userId, ct);
        if (job is null)
        {
            return false; // not found or not the caller's job — no cross-user cancellation
        }

        if (job.Status is JobStatus.Completed or JobStatus.Cancelled or JobStatus.Failed)
        {
            return true; // already terminal — nothing to cancel, but report success (idempotent)
        }

        // Raise the durable cancel flag — the source of truth. The cooperative token is unreliable (the
        // workflow can ignore it), so this persisted flag is what actually stops the run: the pipeline
        // activities poll it and bail, the worker re-checks it before committing a result, and the periodic
        // sweep force-cancels the job if it's still "running". A queued job is marked cancelled outright so
        // the worker skips it when dequeued.
        job.CancelRequested = true;
        var wasRunning = _queue.RequestCancel(jobId);
        if (!wasRunning && job.Status == JobStatus.Queued)
        {
            job.Status = JobStatus.Cancelled;
            job.CompletedAt = DateTimeOffset.UtcNow;
        }

        await _db.SaveChangesAsync(ct);
        return true;
    }
}
