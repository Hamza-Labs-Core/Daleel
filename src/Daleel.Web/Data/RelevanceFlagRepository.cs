using Microsoft.EntityFrameworkCore;

namespace Daleel.Web.Data;

/// <summary>Persists and queries <see cref="RelevanceFlag"/>s — the "not relevant" signal the learning loop reads.</summary>
public interface IRelevanceFlagRepository
{
    /// <summary>Records a flag, idempotent on (UserHash, QueryKey, DedupKey) — a re-flag is a no-op.</summary>
    Task AddAsync(RelevanceFlag flag, CancellationToken ct = default);

    /// <summary>Recent flags for a query (and market), newest first — the negatives fed into the relevance gate.</summary>
    Task<IReadOnlyList<RelevanceFlag>> RecentNegativesAsync(
        string query, string? geo, int take, CancellationToken ct = default);

    /// <summary>Count of flags since a moment (admin metric).</summary>
    Task<int> CountSinceAsync(DateTimeOffset since, CancellationToken ct = default);

    /// <summary>Most recent flags across all queries (admin surface).</summary>
    Task<IReadOnlyList<RelevanceFlag>> ListRecentAsync(int take, CancellationToken ct = default);
}

/// <summary>Transient (Blazor DbContext concurrency) EF-backed <see cref="IRelevanceFlagRepository"/>.</summary>
public sealed class RelevanceFlagRepository : IRelevanceFlagRepository
{
    private readonly DaleelDbContext _db;

    public RelevanceFlagRepository(DaleelDbContext db) => _db = db;

    public async Task AddAsync(RelevanceFlag flag, CancellationToken ct = default)
    {
        // Idempotent: a user can't flag the same item for the same query twice. Pre-check + swallow the
        // unique-index race (two requests flagging at once).
        var exists = await _db.RelevanceFlags.AsNoTracking().AnyAsync(
            f => f.UserHash == flag.UserHash && f.QueryKey == flag.QueryKey && f.DedupKey == flag.DedupKey, ct)
            .ConfigureAwait(false);
        if (exists)
        {
            return;
        }

        _db.RelevanceFlags.Add(flag);
        try
        {
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // Lost the race to the unique index — the flag already exists, which is the desired end state.
        }
    }

    public async Task<IReadOnlyList<RelevanceFlag>> RecentNegativesAsync(
        string query, string? geo, int take, CancellationToken ct = default)
    {
        var key = RelevanceFlag.QueryKeyOf(query);
        var q = _db.RelevanceFlags.AsNoTracking().Where(f => f.QueryKey == key);
        if (!string.IsNullOrWhiteSpace(geo))
        {
            q = q.Where(f => f.Geo == geo || f.Geo == null);
        }

        return await q.OrderByDescending(f => f.CreatedAt).Take(take).ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<int> CountSinceAsync(DateTimeOffset since, CancellationToken ct = default) =>
        await _db.RelevanceFlags.AsNoTracking().CountAsync(f => f.CreatedAt >= since, ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<RelevanceFlag>> ListRecentAsync(int take, CancellationToken ct = default) =>
        await _db.RelevanceFlags.AsNoTracking()
            .OrderByDescending(f => f.CreatedAt).Take(take).ToListAsync(ct).ConfigureAwait(false);
}
