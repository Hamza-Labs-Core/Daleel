using Daleel.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Daleel.Web.Conversation;

/// <summary>Persists the user's single active conversation so every device renders the same state.</summary>
public interface IConversationStore
{
    Task<UserConversation?> GetAsync(string userId, CancellationToken ct = default);
    Task SetRunningAsync(string userId, int jobId, string query, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>
    /// Terminal write for the conversation — applied ONLY while the row still belongs to
    /// <paramref name="jobId"/>. With jobs running concurrently, a slow/old job (or its detached
    /// enrichment, up to minutes later) must never overwrite the conversation a NEWER search owns —
    /// that mislabeled one search's results with another's query.
    /// </summary>
    Task CompleteAsync(string userId, int jobId, string status, string? resultJson, string? resultType, DateTimeOffset now, CancellationToken ct = default);
}

public sealed class ConversationStore : IConversationStore
{
    private readonly DaleelDbContext _db;
    public ConversationStore(DaleelDbContext db) => _db = db;

    public Task<UserConversation?> GetAsync(string userId, CancellationToken ct = default) =>
        _db.UserConversations.AsNoTracking().FirstOrDefaultAsync(c => c.UserId == userId, ct);

    public async Task SetRunningAsync(string userId, int jobId, string query, DateTimeOffset now, CancellationToken ct = default)
    {
        var convo = await _db.UserConversations.FirstOrDefaultAsync(c => c.UserId == userId, ct);
        if (convo is null)
        {
            convo = new UserConversation { UserId = userId };
            _db.UserConversations.Add(convo);
        }

        convo.CurrentJobId = jobId;
        convo.CurrentQuery = query;
        convo.CurrentStatus = "running";
        convo.CurrentResultJson = null;
        convo.CurrentResultType = null;
        convo.StartedAt = now;
        convo.CompletedAt = null;
        await _db.SaveChangesAsync(ct);
    }

    public async Task CompleteAsync(string userId, int jobId, string status, string? resultJson, string? resultType, DateTimeOffset now, CancellationToken ct = default)
    {
        // Job-scoped guard: only the job that still OWNS the conversation may finalize it. A stale
        // job finishing (or late-enriching) after a newer search started is a silent no-op.
        var convo = await _db.UserConversations
            .FirstOrDefaultAsync(c => c.UserId == userId && c.CurrentJobId == jobId, ct);
        if (convo is null)
        {
            return;
        }

        convo.CurrentStatus = status;
        convo.CurrentResultJson = resultJson;
        convo.CurrentResultType = resultType;
        convo.CompletedAt = now;
        await _db.SaveChangesAsync(ct);
    }
}
