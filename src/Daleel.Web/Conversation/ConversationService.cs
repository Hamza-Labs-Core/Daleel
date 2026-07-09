using Daleel.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Daleel.Web.Conversation;

/// <summary>Outcome of submitting a search: accepted with a job id, or rejected with a reason.</summary>
public sealed record SubmitResult(bool Accepted, int? JobId, string? Error, int StatusCode);

/// <summary>
/// Front door for the async conversation flow. Enforces the quota and creates the job. "Enqueuing" is just
/// inserting a <see cref="SearchJob"/> row with <c>Status = queued</c> — the background worker polls Postgres
/// for those, so there is no in-memory queue. Shared by the /api/search endpoint and the interactive
/// conversation page so both behave the same.
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
    private readonly ISystemConfigService? _config;
    private readonly Daleel.Web.Moderation.IQueryPreScreen? _preScreen;

    // _config and _preScreen are optional so tests can construct without them; DI supplies both in the
    // app (_config provides the per-plan default model ids; _preScreen the zero-cost haram gate).
    public ConversationService(
        DaleelDbContext db, IQuotaService quota, ISystemConfigService? config = null,
        Daleel.Web.Moderation.IQueryPreScreen? preScreen = null)
    {
        _db = db;
        _quota = quota;
        _config = config;
        _preScreen = preScreen;
    }

    public async Task<SubmitResult> SubmitAsync(
        string userId, bool isAdmin, string query, string? geo, string? model, string? language = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return new SubmitResult(false, null, "Query is required.", 400);
        }

        // R3/R6: zero-cost pre-screen BEFORE any quota debit or provider/LLM spend. A haram consumable
        // ("beer") is rejected at the door (no SearchJob, no cost); a riba/financial-PRODUCT query is
        // steered to sharia-compliant terms so results surface Islamic options.
        if (_preScreen is not null)
        {
            var screen = await _preScreen.ScreenAsync(query, ct);
            if (screen.Blocked)
            {
                return new SubmitResult(false, null,
                    "We can't search for that — it isn't halal-compliant.", 422);
            }

            if (!string.IsNullOrWhiteSpace(screen.SteeredQuery))
            {
                query = screen.SteeredQuery!;
            }
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
        // Inserting the row with Status=queued IS the enqueue: the background worker polls Postgres for
        // queued jobs and claims them. No in-memory queue means a restart can't lose a pending search.
        _db.SearchJobs.Add(job);
        await _db.SaveChangesAsync(ct);

        // No up-front consume: credits are charged for the actual provider calls once the job finishes
        // (see SearchJobService), so the CanSearch pre-check above is the gate.
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

        // Raise the durable cancel flag — the source of truth and the ONLY cancellation mechanism now that
        // there's no in-memory CTS. This persisted flag is what actually stops the run: the pipeline
        // activities poll it per step and bail, the worker re-checks it before committing a result, and the
        // periodic sweep force-cancels the job if it's still "running". A queued job is marked cancelled
        // outright so the worker's claim query (which only picks "queued") never picks it up.
        job.CancelRequested = true;
        if (job.Status == JobStatus.Queued)
        {
            job.Status = JobStatus.Cancelled;
            job.CompletedAt = DateTimeOffset.UtcNow;
        }

        await _db.SaveChangesAsync(ct);
        return true;
    }
}
