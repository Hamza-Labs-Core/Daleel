using Microsoft.EntityFrameworkCore;

namespace Daleel.Web.Data;

/// <summary>
/// Persistence for saved <see cref="Brand"/> profiles. Lookups and upserts are keyed by the
/// normalized brand name so the same brand is never stored twice, and the staleness query backs the
/// periodic refresh (re-research profiles older than the TTL).
/// </summary>
public interface IBrandRepository
{
    /// <summary>The saved profile for a brand (case/whitespace-insensitive), or null if not researched yet.</summary>
    Task<Brand?> GetByNameAsync(string name, CancellationToken ct = default);

    /// <summary>Inserts a new profile or overwrites the existing one for the same normalized name.</summary>
    Task<Brand> UpsertAsync(Brand brand, CancellationToken ct = default);

    /// <summary>All saved brand profiles, newest-refreshed first (for the admin list).</summary>
    Task<IReadOnlyList<Brand>> ListAsync(CancellationToken ct = default);

    /// <summary>Up to <paramref name="max"/> profiles last refreshed before <paramref name="olderThan"/>, oldest first.</summary>
    Task<IReadOnlyList<Brand>> ListStaleAsync(DateTimeOffset olderThan, int max, CancellationToken ct = default);

    Task<int> CountAsync(CancellationToken ct = default);
}

public sealed class BrandRepository : IBrandRepository
{
    private readonly DaleelDbContext _db;

    public BrandRepository(DaleelDbContext db) => _db = db;

    public Task<Brand?> GetByNameAsync(string name, CancellationToken ct = default)
    {
        var key = Brand.Normalize(name);
        return _db.Brands.AsNoTracking().FirstOrDefaultAsync(b => b.NameKey == key, ct);
    }

    public async Task<Brand> UpsertAsync(Brand brand, CancellationToken ct = default)
    {
        var key = Brand.Normalize(brand.Name);
        var existing = await _db.Brands.FirstOrDefaultAsync(b => b.NameKey == key, ct);
        if (existing is null)
        {
            brand.NameKey = key;
            _db.Brands.Add(brand);
            await _db.SaveChangesAsync(ct);
            return brand;
        }

        existing.Name = brand.Name;
        existing.CountryOfOrigin = brand.CountryOfOrigin;
        existing.ReputationScore = brand.ReputationScore;
        existing.Description = brand.Description;
        existing.Pros = brand.Pros;
        existing.Cons = brand.Cons;
        existing.PopularModels = brand.PopularModels;
        existing.PriceRange = brand.PriceRange;
        existing.Website = brand.Website;
        existing.LastRefreshed = brand.LastRefreshed;
        await _db.SaveChangesAsync(ct);
        return existing;
    }

    public async Task<IReadOnlyList<Brand>> ListAsync(CancellationToken ct = default) =>
        await _db.Brands.AsNoTracking()
            .OrderByDescending(b => b.LastRefreshed)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Brand>> ListStaleAsync(
        DateTimeOffset olderThan, int max, CancellationToken ct = default) =>
        await _db.Brands.AsNoTracking()
            // LastRefreshed persists as Unix-ms, so EF translates this comparison to an integer
            // WHERE that SQLite can run (a raw DateTimeOffset comparison can't be translated).
            .Where(b => b.LastRefreshed < olderThan)
            .OrderBy(b => b.LastRefreshed)
            .Take(max)
            .ToListAsync(ct);

    public Task<int> CountAsync(CancellationToken ct = default) => _db.Brands.CountAsync(ct);
}
