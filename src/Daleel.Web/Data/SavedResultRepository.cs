using Microsoft.EntityFrameworkCore;

namespace Daleel.Web.Data;

/// <summary>
/// Persistence for per-user saved (bookmarked) results. Like the history repository, every method
/// is scoped to a <c>userId</c> so no user can read or mutate another's saved items.
/// </summary>
public interface ISavedResultRepository
{
    Task<SavedResult> AddAsync(SavedResult result, CancellationToken ct = default);

    /// <summary>All saved results for one user, newest first.</summary>
    Task<IReadOnlyList<SavedResult>> ListAsync(string userId, CancellationToken ct = default);

    /// <summary>A single saved result, or null if missing OR not owned by <paramref name="userId"/>.</summary>
    Task<SavedResult?> GetAsync(string userId, int id, CancellationToken ct = default);

    /// <summary>Deletes one saved result if owned by the user. Returns true when a row was removed.</summary>
    Task<bool> DeleteAsync(string userId, int id, CancellationToken ct = default);

    /// <summary>How many results this user has saved — used to enforce the per-plan saved-results cap.</summary>
    Task<int> CountForUserAsync(string userId, CancellationToken ct = default);

    /// <summary>Aggregate count across all users — for admin stats only.</summary>
    Task<int> TotalCountAsync(CancellationToken ct = default);
}

public sealed class SavedResultRepository : ISavedResultRepository
{
    private readonly DaleelDbContext _db;

    public SavedResultRepository(DaleelDbContext db) => _db = db;

    public async Task<SavedResult> AddAsync(SavedResult result, CancellationToken ct = default)
    {
        _db.SavedResults.Add(result);
        await _db.SaveChangesAsync(ct);
        return result;
    }

    public async Task<IReadOnlyList<SavedResult>> ListAsync(string userId, CancellationToken ct = default) =>
        await _db.SavedResults.AsNoTracking()
            .Where(x => x.UserId == userId)
            .OrderByDescending(x => x.Id) // Order by the monotonic identity key; Id desc = newest first
            .ToListAsync(ct);

    public Task<SavedResult?> GetAsync(string userId, int id, CancellationToken ct = default) =>
        _db.SavedResults.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct);

    public async Task<bool> DeleteAsync(string userId, int id, CancellationToken ct = default)
    {
        var row = await _db.SavedResults.FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct);
        if (row is null)
        {
            return false;
        }

        _db.SavedResults.Remove(row);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public Task<int> CountForUserAsync(string userId, CancellationToken ct = default) =>
        _db.SavedResults.CountAsync(x => x.UserId == userId, ct);

    public Task<int> TotalCountAsync(CancellationToken ct = default) => _db.SavedResults.CountAsync(ct);
}
