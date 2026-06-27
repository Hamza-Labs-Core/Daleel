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

    /// <summary>The saved profile with this database id, or null if no such row exists.</summary>
    Task<Brand?> GetByIdAsync(int id, CancellationToken ct = default);

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

    public Task<Brand?> GetByIdAsync(int id, CancellationToken ct = default) =>
        _db.Brands.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct);

    public async Task<Brand> UpsertAsync(Brand brand, CancellationToken ct = default)
    {
        var key = Brand.Normalize(brand.Name);
        var existing = await _db.Brands.FirstOrDefaultAsync(b => b.NameKey == key, ct);
        if (existing is not null)
        {
            ApplyUpdates(existing, brand);
            await _db.SaveChangesAsync(ct);
            return existing;
        }

        brand.NameKey = key;
        _db.Brands.Add(brand);
        try
        {
            await _db.SaveChangesAsync(ct);
            return brand;
        }
        catch (DbUpdateException)
        {
            // A concurrent insert for the same NameKey won the unique-index race. Reset the WHOLE change
            // tracker (not just our failed entity) so a poisoned tracker from this failed SaveChanges can't
            // cascade into a later upsert on the same shared request-scoped context and silently drop rows;
            // then reload the row the other writer committed and merge our values onto it.
            _db.ChangeTracker.Clear();
            var winner = await _db.Brands.FirstOrDefaultAsync(b => b.NameKey == key, ct);
            if (winner is null)
            {
                throw; // not the race we expected (no row to update) — surface the original failure
            }

            ApplyUpdates(winner, brand);
            await _db.SaveChangesAsync(ct);
            return winner;
        }
    }

    private static void ApplyUpdates(Brand existing, Brand brand)
    {
        // Null-coalesce optional fields so a partial re-research (the LLM/Context.dev momentarily returning
        // less) doesn't blank out previously-good profile data. "Absent this harvest" means "unknown", not
        // "deleted" — only overwrite a field when the fresh research actually carries a value. The list
        // columns are kept unless the harvest brought a non-empty replacement.
        if (!string.IsNullOrWhiteSpace(brand.Name))
        {
            existing.Name = brand.Name;
        }

        existing.CountryOfOrigin = brand.CountryOfOrigin ?? existing.CountryOfOrigin;
        existing.ReputationScore = brand.ReputationScore ?? existing.ReputationScore;
        existing.Description = brand.Description ?? existing.Description;
        existing.Pros = brand.Pros.Count > 0 ? brand.Pros : existing.Pros;
        existing.Cons = brand.Cons.Count > 0 ? brand.Cons : existing.Cons;
        existing.PopularModels = brand.PopularModels.Count > 0 ? brand.PopularModels : existing.PopularModels;
        existing.PriceRange = brand.PriceRange ?? existing.PriceRange;
        existing.Website = brand.Website ?? existing.Website;
        existing.LastRefreshed = brand.LastRefreshed;
    }

    public async Task<IReadOnlyList<Brand>> ListAsync(CancellationToken ct = default) =>
        await _db.Brands.AsNoTracking()
            .OrderByDescending(b => b.LastRefreshed)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Brand>> ListStaleAsync(
        DateTimeOffset olderThan, int max, CancellationToken ct = default) =>
        await _db.Brands.AsNoTracking()
            // LastRefreshed persists as Unix-ms, so EF translates this comparison to an integer
            // WHERE the provider can run (a raw DateTimeOffset comparison may not translate).
            .Where(b => b.LastRefreshed < olderThan)
            .OrderBy(b => b.LastRefreshed)
            .Take(max)
            .ToListAsync(ct);

    public Task<int> CountAsync(CancellationToken ct = default) => _db.Brands.CountAsync(ct);
}
