using Microsoft.EntityFrameworkCore;

namespace Daleel.Web.Data;

/// <summary>A page of results plus the total count, for server-side pagination.</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize)
{
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}

/// <summary>
/// Persistence for per-user search history. Every method takes the caller's <c>userId</c> and
/// filters on it — there is no overload that reads "all rows", which is what guarantees one user
/// can never observe another's history.
/// </summary>
public interface ISearchHistoryRepository
{
    Task<SearchHistoryEntry> AddAsync(SearchHistoryEntry entry, CancellationToken ct = default);

    /// <summary>Paginated history for one user, newest first, optionally filtered by query text.</summary>
    Task<PagedResult<SearchHistoryEntry>> ListAsync(
        string userId, string? search = null, int page = 1, int pageSize = 20, CancellationToken ct = default);

    /// <summary>A single entry, or null if it does not exist OR is not owned by <paramref name="userId"/>.</summary>
    Task<SearchHistoryEntry?> GetAsync(string userId, int id, CancellationToken ct = default);

    /// <summary>Deletes one entry if owned by the user. Returns true when a row was removed.</summary>
    Task<bool> DeleteAsync(string userId, int id, CancellationToken ct = default);

    /// <summary>Clears all history for one user. Returns the number of rows removed.</summary>
    Task<int> ClearAsync(string userId, CancellationToken ct = default);

    /// <summary>Aggregate count across all users — for admin stats only (no row data exposed).</summary>
    Task<int> TotalCountAsync(CancellationToken ct = default);

    /// <summary>
    /// Replaces the stored result on ONE specific history row (owner-checked). Called after background
    /// enrichment — targeted by the row id captured when the entry was added, never by query text,
    /// so a late-landing enrichment can't overwrite a newer same-text row (repeat search, market
    /// switch, or another feature's entry). Returns true when the row was updated.
    /// </summary>
    Task<bool> UpdateResultAsync(string userId, int id, string resultJson, CancellationToken ct = default);
}

public sealed class SearchHistoryRepository : ISearchHistoryRepository
{
    private readonly DaleelDbContext _db;

    public SearchHistoryRepository(DaleelDbContext db) => _db = db;

    public async Task<SearchHistoryEntry> AddAsync(SearchHistoryEntry entry, CancellationToken ct = default)
    {
        _db.SearchHistory.Add(entry);
        await _db.SaveChangesAsync(ct);
        return entry;
    }

    public async Task<bool> UpdateResultAsync(
        string userId, int id, string resultJson, CancellationToken ct = default)
    {
        // Owner filter first (the isolation boundary), then the exact row.
        var entry = await _db.SearchHistory
            .FirstOrDefaultAsync(x => x.UserId == userId && x.Id == id, ct);
        if (entry is null)
        {
            return false;
        }

        entry.ResultJson = resultJson;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<PagedResult<SearchHistoryEntry>> ListAsync(
        string userId, string? search = null, int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        // The user filter is applied first and is non-optional — this is the isolation boundary.
        var query = _db.SearchHistory.AsNoTracking().Where(x => x.UserId == userId);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(x => EF.Functions.Like(x.Query, $"%{term}%"));
        }

        var total = await query.CountAsync(ct);
        // Order by the identity key (monotonic with insertion) rather than CreatedAt: for append-only
        // history Id desc == newest first, a stable provider-agnostic ordering.
        var items = await query
            .OrderByDescending(x => x.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return new PagedResult<SearchHistoryEntry>(items, total, page, pageSize);
    }

    public Task<SearchHistoryEntry?> GetAsync(string userId, int id, CancellationToken ct = default) =>
        _db.SearchHistory.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct);

    public async Task<bool> DeleteAsync(string userId, int id, CancellationToken ct = default)
    {
        var entry = await _db.SearchHistory.FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct);
        if (entry is null)
        {
            return false;
        }

        _db.SearchHistory.Remove(entry);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<int> ClearAsync(string userId, CancellationToken ct = default)
    {
        var rows = await _db.SearchHistory.Where(x => x.UserId == userId).ToListAsync(ct);
        _db.SearchHistory.RemoveRange(rows);
        await _db.SaveChangesAsync(ct);
        return rows.Count;
    }

    public Task<int> TotalCountAsync(CancellationToken ct = default) => _db.SearchHistory.CountAsync(ct);
}
