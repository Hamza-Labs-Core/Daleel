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

    /// <summary>The LIVE (non-alias) row with this identity key, or null. The save path uses this to
    /// converge a re-extracted duplicate onto the existing entity instead of inserting a new row.</summary>
    Task<EntityRecord?> GetByIdentityKeyAsync(string identityKey, CancellationToken ct = default);

    /// <summary>Name-searched page of entity index rows, newest first (the public /items directory
    /// and the B2B /api/v1/items endpoint).</summary>
    Task<IReadOnlyList<EntityRecord>> SearchAsync(
        string? query, string? intent, int skip, int take,
        string? geo = null, string? category = null, int? brandId = null, int? storeId = null,
        CancellationToken ct = default);

    /// <summary>Distinct geo markets present for an intent (the /items location filter's options).</summary>
    Task<IReadOnlyList<string>> DistinctGeosAsync(string intent, CancellationToken ct = default);

    /// <summary>Distinct categories present for an intent (the /items category filter's options).</summary>
    Task<IReadOnlyList<string>> DistinctCategoriesAsync(string intent, CancellationToken ct = default);

    /// <summary>The brands (id + name) that have items in a category — the meta filter that switches
    /// with the selected category.</summary>
    Task<IReadOnlyList<(int Id, string Name)>> BrandsInCategoryAsync(
        string intent, string category, CancellationToken ct = default);
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

    public Task<EntityRecord?> GetByIdentityKeyAsync(string identityKey, CancellationToken ct = default) =>
        _db.EntityRecords.AsNoTracking()
            .Where(r => r.IdentityKey == identityKey && r.MergedIntoId == null)
            .OrderByDescending(r => r.LastRefreshed)
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<EntityRecord>> SearchAsync(
        string? query, string? intent, int skip, int take,
        string? geo = null, string? category = null, int? brandId = null, int? storeId = null,
        CancellationToken ct = default)
    {
        // Alias rows (merged duplicates) never surface in the directory.
        IQueryable<EntityRecord> q = _db.EntityRecords.AsNoTracking().Where(r => r.MergedIntoId == null);
        if (!string.IsNullOrWhiteSpace(intent))
        {
            q = q.Where(r => r.Intent == intent);
        }

        if (!string.IsNullOrWhiteSpace(geo))
        {
            q = q.Where(r => r.Geo == geo);
        }

        if (!string.IsNullOrWhiteSpace(category))
        {
            q = q.Where(r => r.Category == category);
        }

        if (brandId is { } bid)
        {
            q = q.Where(r => r.BrandId == bid);
        }

        if (storeId is { } sid)
        {
            q = q.Where(r => r.StoreId == sid);
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            var needle = $"%{query.Trim()}%";
            q = q.Where(r => EF.Functions.ILike(r.Name, needle));
        }

        return await q.OrderByDescending(r => r.LastRefreshed).Skip(skip).Take(take).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<string>> DistinctGeosAsync(string intent, CancellationToken ct = default) =>
        await _db.EntityRecords.AsNoTracking()
            .Where(r => r.Intent == intent && r.MergedIntoId == null && r.Geo != null && r.Geo != "")
            .Select(r => r.Geo!)
            .Distinct()
            .OrderBy(g => g)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<string>> DistinctCategoriesAsync(string intent, CancellationToken ct = default) =>
        await _db.EntityRecords.AsNoTracking()
            .Where(r => r.Intent == intent && r.MergedIntoId == null && r.Category != null && r.Category != "")
            .Select(r => r.Category!)
            .Distinct()
            .OrderBy(c => c)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<(int Id, string Name)>> BrandsInCategoryAsync(
        string intent, string category, CancellationToken ct = default)
    {
        var rows = await _db.EntityRecords.AsNoTracking()
            .Where(r => r.Intent == intent && r.MergedIntoId == null && r.Category == category && r.BrandId != null)
            .Join(_db.Brands.AsNoTracking(), r => r.BrandId, b => b.Id, (r, b) => new { b.Id, b.Name })
            .Distinct()
            .OrderBy(x => x.Name)
            .ToListAsync(ct);
        return rows.Select(x => (x.Id, x.Name)).ToList();
    }

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
        existing.Category = fresh.Category ?? existing.Category;
        existing.IdentityKey = fresh.IdentityKey ?? existing.IdentityKey;
        // An alias marker is carried, never cleared here — un-aliasing is the worker's job alone.
        existing.MergedIntoId = fresh.MergedIntoId ?? existing.MergedIntoId;

        // Relations: only ever fill in a relation we've now resolved; never blank an existing link just
        // because this pass couldn't resolve it (the brand row may simply not be created yet).
        existing.SearchId = fresh.SearchId ?? existing.SearchId;
        existing.BrandId = fresh.BrandId ?? existing.BrandId;
        existing.StoreId = fresh.StoreId ?? existing.StoreId;
        existing.ProductKey = fresh.ProductKey ?? existing.ProductKey;
        existing.ParentProductKey = fresh.ParentProductKey ?? existing.ParentProductKey;
    }
}
