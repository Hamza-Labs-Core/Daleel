using Microsoft.EntityFrameworkCore;

namespace Daleel.Web.Data;

/// <summary>
/// Persistence for a brand's discovered site hierarchy (<see cref="BrandSite"/>). Upserts are keyed
/// by (BrandId, Level, CountryCode) so re-discovering a level updates its row in place — one global
/// row, one regional row per hint, one local row per market.
/// </summary>
public interface IBrandSiteRepository
{
    /// <summary>All recorded sites for a brand (every level/market), most-recently-refreshed first.</summary>
    Task<IReadOnlyList<BrandSite>> GetForBrandAsync(int brandId, CancellationToken ct = default);

    /// <summary>Inserts a site or overwrites the existing row for the same (brand, level, country).</summary>
    Task<BrandSite> UpsertAsync(BrandSite site, CancellationToken ct = default);
}

public sealed class BrandSiteRepository : IBrandSiteRepository
{
    private readonly DaleelDbContext _db;

    public BrandSiteRepository(DaleelDbContext db) => _db = db;

    public async Task<IReadOnlyList<BrandSite>> GetForBrandAsync(int brandId, CancellationToken ct = default) =>
        await _db.BrandSites.AsNoTracking()
            .Where(s => s.BrandId == brandId)
            .OrderByDescending(s => s.LastRefreshed)
            .ToListAsync(ct);

    public async Task<BrandSite> UpsertAsync(BrandSite site, CancellationToken ct = default)
    {
        var existing = await _db.BrandSites.FirstOrDefaultAsync(
            s => s.BrandId == site.BrandId && s.Level == site.Level && s.CountryCode == site.CountryCode, ct);
        if (existing is not null)
        {
            ApplyUpdates(existing, site);
            await _db.SaveChangesAsync(ct);
            return existing;
        }

        _db.BrandSites.Add(site);
        try
        {
            await _db.SaveChangesAsync(ct);
            return site;
        }
        catch (DbUpdateException)
        {
            // A concurrent insert won the (BrandId, Level, CountryCode) unique-index race. Reset the
            // WHOLE change tracker (not just our failed entity) so a poisoned tracker can't cascade
            // into a later save on the same shared context; then reload the committed row and merge
            // our values onto it. Same recovery as BrandRepository/BrandModelRepository.
            _db.ChangeTracker.Clear();
            var winner = await _db.BrandSites.FirstOrDefaultAsync(
                s => s.BrandId == site.BrandId && s.Level == site.Level && s.CountryCode == site.CountryCode, ct);
            if (winner is null)
            {
                throw; // not the race we expected (no row to update) — surface the original failure
            }

            ApplyUpdates(winner, site);
            await _db.SaveChangesAsync(ct);
            return winner;
        }
    }

    private static void ApplyUpdates(BrandSite existing, BrandSite site)
    {
        // A re-discovery that somehow carries no URL means "unknown", not "deleted" — keep the
        // known one. LastRefreshed is always a current-discovery fact.
        if (!string.IsNullOrWhiteSpace(site.Url))
        {
            existing.Url = site.Url;
        }

        existing.LastRefreshed = site.LastRefreshed;
    }
}
