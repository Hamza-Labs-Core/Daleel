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
    Task<SubmitResult> SubmitAsync(string userId, bool isAdmin, string query, string? geo, string? model, CancellationToken ct = default);
    Task<bool> CancelAsync(string userId, int jobId, CancellationToken ct = default);
}

public sealed class ConversationService : IConversationService
{
    private readonly DaleelDbContext _db;
    private readonly IQuotaService _quota;
    private readonly ISearchJobQueue _queue;

    public ConversationService(DaleelDbContext db, IQuotaService quota, ISearchJobQueue queue)
    {
        _db = db;
        _quota = quota;
        _queue = queue;
    }

    public async Task<SubmitResult> SubmitAsync(
        string userId, bool isAdmin, string query, string? geo, string? model, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return new SubmitResult(false, null, "Query is required.", 400);
        }

        if (!await _quota.TryConsumeAsync(userId, isAdmin, ct))
        {
            var status = await _quota.GetStatusAsync(userId, isAdmin, ct);
            return new SubmitResult(false, null,
                $"You've used all {status.Limit} free searches this month. Upgrade for unlimited access.", 429);
        }

        var job = new SearchJob
        {
            UserId = userId,
            Query = query.Trim(),
            QueryType = "ask",
            Geo = string.IsNullOrWhiteSpace(geo) ? "jordan" : geo,
            Model = model ?? string.Empty,
            Status = JobStatus.Queued,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _db.SearchJobs.Add(job);
        await _db.SaveChangesAsync(ct);

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

        // Interrupt it if running; if still queued, mark cancelled so the worker skips it.
        if (!_queue.RequestCancel(jobId) && job.Status == JobStatus.Queued)
        {
            job.Status = JobStatus.Cancelled;
            job.CompletedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
        }

        return true;
    }
}
