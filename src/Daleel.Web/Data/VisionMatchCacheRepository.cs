using Microsoft.EntityFrameworkCore;

namespace Daleel.Web.Data;

/// <summary>
/// Persistence for <see cref="VisionMatchCache"/> — the memo of which (store image, brand model) pairs
/// have already been compared by the vision model. The smart identifier reads this before every vision
/// call so the same image pair is never sent twice.
/// </summary>
public interface IVisionMatchCacheRepository
{
    /// <summary>The cached verdict for a (store image hash, brand model) pair, or null if never compared.</summary>
    Task<VisionMatchCache?> GetAsync(string storeImageHash, int brandModelId, CancellationToken ct = default);

    /// <summary>Records (or overwrites) the verdict for a pair. Idempotent on the unique key.</summary>
    Task<VisionMatchCache> UpsertAsync(VisionMatchCache entry, CancellationToken ct = default);

    Task<int> CountAsync(CancellationToken ct = default);
}

public sealed class VisionMatchCacheRepository : IVisionMatchCacheRepository
{
    private readonly DaleelDbContext _db;

    public VisionMatchCacheRepository(DaleelDbContext db) => _db = db;

    public Task<VisionMatchCache?> GetAsync(string storeImageHash, int brandModelId, CancellationToken ct = default) =>
        _db.VisionMatchCaches.AsNoTracking()
            .FirstOrDefaultAsync(v => v.StoreImageHash == storeImageHash && v.BrandModelId == brandModelId, ct);

    public async Task<VisionMatchCache> UpsertAsync(VisionMatchCache entry, CancellationToken ct = default)
    {
        var existing = await _db.VisionMatchCaches
            .FirstOrDefaultAsync(v => v.StoreImageHash == entry.StoreImageHash && v.BrandModelId == entry.BrandModelId, ct);
        if (existing is not null)
        {
            existing.Confidence = entry.Confidence;
            existing.MatchedModelName = entry.MatchedModelName;
            existing.MatchedAt = entry.MatchedAt;
            await _db.SaveChangesAsync(ct);
            return existing;
        }

        _db.VisionMatchCaches.Add(entry);
        try
        {
            await _db.SaveChangesAsync(ct);
            return entry;
        }
        catch (DbUpdateException)
        {
            // A concurrent matcher won the (hash, model) unique-index race: drop ours and return the winner.
            _db.Entry(entry).State = EntityState.Detached;
            var winner = await _db.VisionMatchCaches
                .FirstOrDefaultAsync(v => v.StoreImageHash == entry.StoreImageHash && v.BrandModelId == entry.BrandModelId, ct);
            if (winner is null)
            {
                throw;
            }

            return winner;
        }
    }

    public Task<int> CountAsync(CancellationToken ct = default) => _db.VisionMatchCaches.CountAsync(ct);
}
