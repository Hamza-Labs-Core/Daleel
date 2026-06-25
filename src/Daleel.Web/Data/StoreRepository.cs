using Microsoft.EntityFrameworkCore;

namespace Daleel.Web.Data;

/// <summary>Persistence for saved <see cref="Store"/> profiles. Mirrors <see cref="IBrandRepository"/>.</summary>
public interface IStoreRepository
{
    Task<Store?> GetByNameAsync(string name, CancellationToken ct = default);
    Task<Store> UpsertAsync(Store store, CancellationToken ct = default);
    Task<IReadOnlyList<Store>> ListAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Store>> ListStaleAsync(DateTimeOffset olderThan, int max, CancellationToken ct = default);
    Task<int> CountAsync(CancellationToken ct = default);
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
}
