using Microsoft.EntityFrameworkCore;

namespace Daleel.Web.Data;

/// <summary>
/// Persistence for saved <see cref="ProductProfile"/> deep-dives, keyed by the normalized brand+model
/// so the same product is never stored twice and a fresh profile is reused instead of re-scraped.
/// </summary>
public interface IProductProfileRepository
{
    /// <summary>The saved deep-dive for a normalized key, or null if this product hasn't been dived yet.</summary>
    Task<ProductProfile?> GetByKeyAsync(string key, CancellationToken ct = default);

    /// <summary>Inserts a new deep-dive or overwrites the existing one for the same normalized key.</summary>
    Task<ProductProfile> UpsertAsync(ProductProfile profile, CancellationToken ct = default);

    Task<int> CountAsync(CancellationToken ct = default);
}

public sealed class ProductProfileRepository : IProductProfileRepository
{
    private readonly DaleelDbContext _db;

    public ProductProfileRepository(DaleelDbContext db) => _db = db;

    public Task<ProductProfile?> GetByKeyAsync(string key, CancellationToken ct = default) =>
        _db.ProductProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.NameKey == key, ct);

    public async Task<ProductProfile> UpsertAsync(ProductProfile profile, CancellationToken ct = default)
    {
        var key = string.IsNullOrEmpty(profile.NameKey)
            ? ProductProfile.KeyFor(profile.Brand, profile.Model, profile.Name)
            : profile.NameKey;
        profile.NameKey = key;

        var existing = await _db.ProductProfiles.FirstOrDefaultAsync(p => p.NameKey == key, ct);
        if (existing is not null)
        {
            ApplyUpdates(existing, profile);
            await _db.SaveChangesAsync(ct);
            return existing;
        }

        _db.ProductProfiles.Add(profile);
        try
        {
            await _db.SaveChangesAsync(ct);
            return profile;
        }
        catch (DbUpdateException)
        {
            // A concurrent insert won the unique-index race: drop ours, reload, merge onto the winner.
            _db.Entry(profile).State = EntityState.Detached;
            var winner = await _db.ProductProfiles.FirstOrDefaultAsync(p => p.NameKey == key, ct);
            if (winner is null)
            {
                throw;
            }

            ApplyUpdates(winner, profile);
            await _db.SaveChangesAsync(ct);
            return winner;
        }
    }

    public Task<int> CountAsync(CancellationToken ct = default) => _db.ProductProfiles.CountAsync(ct);

    // Coalesce nullable fields onto the existing row rather than blind-overwriting (mirrors
    // BrandModelRepository): a deep-dive that re-saves only to add the canonical SpecsJson must not wipe a
    // Details/SourceUrl that an earlier, fresher scrape persisted.
    private static void ApplyUpdates(ProductProfile existing, ProductProfile profile)
    {
        existing.Name = profile.Name;
        existing.Brand = profile.Brand ?? existing.Brand;
        existing.Model = profile.Model ?? existing.Model;
        existing.Details = profile.Details ?? existing.Details;
        existing.SpecsJson = profile.SpecsJson ?? existing.SpecsJson;
        existing.SourceUrl = profile.SourceUrl ?? existing.SourceUrl;
        existing.LastRefreshed = profile.LastRefreshed;
    }
}
