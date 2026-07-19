using Microsoft.EntityFrameworkCore;

namespace Daleel.Web.Data;

/// <summary>Persistence for saved <see cref="Store"/> profiles. Mirrors <see cref="IBrandRepository"/>.</summary>
public interface IStoreRepository
{
    Task<Store?> GetByNameAsync(string name, CancellationToken ct = default);

    /// <summary>The saved profile with this database id, or null if no such row exists.</summary>
    Task<Store?> GetByIdAsync(int id, CancellationToken ct = default);

    Task<Store> UpsertAsync(Store store, CancellationToken ct = default);
    Task<IReadOnlyList<Store>> ListAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Store>> ListStaleAsync(DateTimeOffset olderThan, int max, CancellationToken ct = default);
    Task<int> CountAsync(CancellationToken ct = default);

    /// <summary>Name/location-searched page of store profiles, alphabetical (the public /stores/all directory).</summary>
    Task<IReadOnlyList<Store>> SearchAsync(
        string? query, int skip, int take, string? location = null, string? type = null, CancellationToken ct = default);

    /// <summary>Distinct store locations (the directory's location filter options).</summary>
    Task<IReadOnlyList<string>> DistinctLocationsAsync(CancellationToken ct = default);

    /// <summary>Distinct store types (the directory's category filter options).</summary>
    Task<IReadOnlyList<string>> DistinctTypesAsync(CancellationToken ct = default);
}

public sealed class StoreRepository : IStoreRepository
{
    private readonly DaleelDbContext _db;

    public StoreRepository(DaleelDbContext db) => _db = db;

    public Task<Store?> GetByNameAsync(string name, CancellationToken ct = default)
    {
        var key = Store.Normalize(name);
        return _db.Stores.AsNoTracking().FirstOrDefaultAsync(s => s.NameKey == key, ct);
    }

    public Task<Store?> GetByIdAsync(int id, CancellationToken ct = default) =>
        _db.Stores.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);

    public async Task<Store> UpsertAsync(Store store, CancellationToken ct = default)
    {
        var key = Store.Normalize(store.Name);
        var existing = await _db.Stores.FirstOrDefaultAsync(s => s.NameKey == key, ct);
        if (existing is not null)
        {
            ApplyUpdates(existing, store);
            await _db.SaveChangesAsync(ct);
            return existing;
        }

        store.NameKey = key;
        _db.Stores.Add(store);
        try
        {
            await _db.SaveChangesAsync(ct);
            return store;
        }
        catch (DbUpdateException)
        {
            // A concurrent insert for the same NameKey won the unique-index race. Drop our pending
            // insert, reload the row the other writer committed, and merge our values onto it.
            _db.Entry(store).State = EntityState.Detached;
            var winner = await _db.Stores.FirstOrDefaultAsync(s => s.NameKey == key, ct);
            if (winner is null)
            {
                throw; // not the race we expected (no row to update) — surface the original failure
            }

            ApplyUpdates(winner, store);
            await _db.SaveChangesAsync(ct);
            return winner;
        }
    }

    private static void ApplyUpdates(Store existing, Store store)
    {
        existing.Name = store.Name;
        existing.Location = store.Location;
        existing.Type = store.Type;
        existing.Website = store.Website;
        existing.BrandsCarried = store.BrandsCarried;
        existing.Rating = store.Rating;
        // Contact + Google-Maps verification fields (added with store enrichment).
        existing.Phone = store.Phone;
        existing.Email = store.Email;
        existing.Address = store.Address;
        existing.Latitude = store.Latitude;
        existing.Longitude = store.Longitude;
        existing.OpeningHours = store.OpeningHours;
        existing.GoogleRating = store.GoogleRating;
        existing.GoogleReviewCount = store.GoogleReviewCount;
        existing.GooglePlaceId = store.GooglePlaceId;
        existing.GoogleMapsUrl = store.GoogleMapsUrl;
        existing.LastRefreshed = store.LastRefreshed;
    }

    public async Task<IReadOnlyList<Store>> ListAsync(CancellationToken ct = default) =>
        await _db.Stores.AsNoTracking()
            .OrderByDescending(s => s.LastRefreshed)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Store>> ListStaleAsync(
        DateTimeOffset olderThan, int max, CancellationToken ct = default) =>
        await _db.Stores.AsNoTracking()
            .Where(s => s.LastRefreshed < olderThan)
            .OrderBy(s => s.LastRefreshed)
            .Take(max)
            .ToListAsync(ct);

    public Task<int> CountAsync(CancellationToken ct = default) => _db.Stores.CountAsync(ct);

    public async Task<IReadOnlyList<Store>> SearchAsync(
        string? query, int skip, int take, string? location = null, string? type = null, CancellationToken ct = default)
    {
        IQueryable<Store> q = _db.Stores.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(location))
        {
            q = q.Where(s => s.Location == location);
        }

        if (!string.IsNullOrWhiteSpace(type))
        {
            q = q.Where(s => s.Type == type);
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            var needle = $"%{query.Trim()}%";
            q = q.Where(s => EF.Functions.ILike(s.Name, needle) ||
                             (s.Location != null && EF.Functions.ILike(s.Location, needle)));
        }

        return await q.OrderBy(s => s.Name).Skip(skip).Take(take).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<string>> DistinctLocationsAsync(CancellationToken ct = default) =>
        await _db.Stores.AsNoTracking()
            .Where(s => s.Location != null && s.Location != "")
            .Select(s => s.Location!)
            .Distinct()
            .OrderBy(l => l)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<string>> DistinctTypesAsync(CancellationToken ct = default) =>
        await _db.Stores.AsNoTracking()
            .Where(s => s.Type != null && s.Type != "")
            .Select(s => s.Type!)
            .Distinct()
            .OrderBy(t => t)
            .ToListAsync(ct);
}
