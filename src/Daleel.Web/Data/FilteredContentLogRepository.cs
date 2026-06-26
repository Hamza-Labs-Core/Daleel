using Microsoft.EntityFrameworkCore;

namespace Daleel.Web.Data;

/// <summary>Persists and queries the admin-only <see cref="FilteredContentLog"/>.</summary>
public interface IFilteredContentLogRepository
{
    Task AddBatchAsync(IEnumerable<FilteredContentLog> rows, CancellationToken ct = default);
    Task<IReadOnlyList<FilteredContentLog>> ListRecentAsync(int take, CancellationToken ct = default);
    Task<int> CountSinceAsync(DateTimeOffset since, CancellationToken ct = default);
}

public sealed class FilteredContentLogRepository : IFilteredContentLogRepository
{
    private readonly DaleelDbContext _db;

    public FilteredContentLogRepository(DaleelDbContext db) => _db = db;

    public async Task AddBatchAsync(IEnumerable<FilteredContentLog> rows, CancellationToken ct = default)
    {
        _db.FilteredContentLogs.AddRange(rows);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<FilteredContentLog>> ListRecentAsync(int take, CancellationToken ct = default) =>
        // Order by the auto-increment Id (newest = highest), not CreatedAt: SQLite can't translate
        // ORDER BY on a DateTimeOffset column and throws NotSupportedException, crashing the page.
        await _db.FilteredContentLogs.AsNoTracking()
            .OrderByDescending(x => x.Id)
            .Take(take)
            .ToListAsync(ct);

    public async Task<int> CountSinceAsync(DateTimeOffset since, CancellationToken ct = default) =>
        await _db.FilteredContentLogs.AsNoTracking().CountAsync(x => x.CreatedAt >= since, ct);
}
