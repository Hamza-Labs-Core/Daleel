using Microsoft.EntityFrameworkCore;

namespace Daleel.Web.Data;

/// <summary>
/// Persistence for <see cref="EntityRecord"/> index rows. Upserts are keyed by the entity's stable id
/// (the PK), so re-surfacing the same product/service/place in a later search updates its index row in
/// place (and re-points it at the freshly-written R2 document) rather than duplicating it.
/// </summary>
public interface IEntityRecordRepository
{
    /// <summary>Inserts a new index row or updates the existing one for the same stable id.</summary>
    Task<EntityRecord> UpsertAsync(EntityRecord record, CancellationToken ct = default);

    /// <summary>The index row for a stable id (with its brand/store loaded), or null.</summary>
    Task<EntityRecord?> GetByIdAsync(string id, CancellationToken ct = default);

    /// <summary>All entities a given search run surfaced, most-recently-refreshed first.</summary>
    Task<IReadOnlyList<EntityRecord>> ListBySearchAsync(string searchId, CancellationToken ct = default);

    /// <summary>All entities under a brand (the brand→entities relation), most-recently-refreshed first.</summary>
    Task<IReadOnlyList<EntityRecord>> ListByBrandAsync(int brandId, CancellationToken ct = default);

    Task<int> CountAsync(CancellationToken ct = default);
}

public sealed class EntityRecordRepository : IEntityRecordRepository
{
    private readonly DaleelDbContext _db;

    public EntityRecordRepository(DaleelDbContext db) => _db = db;

    public async Task<EntityRecord> UpsertAsync(EntityRecord record, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(record.Id))
        {
            throw new ArgumentException("EntityRecord.Id is required (it is the primary key).", nameof(record));
        }

        if (string.IsNullOrEmpty(record.NameKey))
        {
            record.NameKey = EntityRecord.Normalize(record.Name);
        }

        var existing = await _db.EntityRecords.FirstOrDefaultAsync(r => r.Id == record.Id, ct);
        if (existing is not null)
        {
            ApplyUpdates(existing, record);
            await _db.SaveChangesAsync(ct);
            return existing;
        }

        _db.EntityRecords.Add(record);
        try
        {
            await _db.SaveChangesAsync(ct);
            return record;
        }
        catch (DbUpdateException)
        {
            // A concurrent insert won the PK race. Reset the whole tracker (this can run on a shared
            // context during fan-out) and merge our values onto the committed winner — same recovery
            // shape as BrandModelRepository.
            _db.ChangeTracker.Clear();
            var winner = await _db.EntityRecords.FirstOrDefaultAsync(r => r.Id == record.Id, ct);
            if (winner is null)
            {
                throw;
            }

            ApplyUpdates(winner, record);
            await _db.SaveChangesAsync(ct);
            return winner;
        }
    }

    public Task<EntityRecord?> GetByIdAsync(string id, CancellationToken ct = default) =>
        _db.EntityRecords.AsNoTracking()
            .Include(r => r.Brand)
            .Include(r => r.Store)
            .FirstOrDefaultAsync(r => r.Id == id, ct);

    public async Task<IReadOnlyList<EntityRecord>> ListBySearchAsync(string searchId, CancellationToken ct = default) =>
        await _db.EntityRecords.AsNoTracking()
            .Where(r => r.SearchId == searchId)
            .OrderByDescending(r => r.LastRefreshed)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<EntityRecord>> ListByBrandAsync(int brandId, CancellationToken ct = default) =>
        await _db.EntityRecords.AsNoTracking()
            .Where(r => r.BrandId == brandId)
            .OrderByDescending(r => r.LastRefreshed)
            .ToListAsync(ct);

    public Task<int> CountAsync(CancellationToken ct = default) => _db.EntityRecords.CountAsync(ct);

    private static void ApplyUpdates(EntityRecord existing, EntityRecord fresh)
    {
        // The R2 document is rewritten on every save, so its pointer is always current-run truth.
        existing.R2Key = fresh.R2Key;
        existing.R2Url = fresh.R2Url ?? existing.R2Url;
        existing.LastRefreshed = fresh.LastRefreshed;

        // Refresh display/lookup metadata when the fresh pass carries it.
        if (!string.IsNullOrWhiteSpace(fresh.Name)) existing.Name = fresh.Name;
        if (!string.IsNullOrWhiteSpace(fresh.NameKey)) existing.NameKey = fresh.NameKey;
        existing.Intent = fresh.Intent;
        existing.Geo = fresh.Geo ?? existing.Geo;

        // Relations: only ever fill in a relation we've now resolved; never blank an existing link just
        // because this pass couldn't resolve it (the brand row may simply not be created yet).
        existing.SearchId = fresh.SearchId ?? existing.SearchId;
        existing.BrandId = fresh.BrandId ?? existing.BrandId;
        existing.StoreId = fresh.StoreId ?? existing.StoreId;
        existing.ProductKey = fresh.ProductKey ?? existing.ProductKey;
        existing.ParentProductKey = fresh.ParentProductKey ?? existing.ParentProductKey;
    }
}
