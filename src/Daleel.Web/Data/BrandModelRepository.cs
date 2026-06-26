using Microsoft.EntityFrameworkCore;

namespace Daleel.Web.Data;

/// <summary>
/// Persistence for <see cref="BrandModel"/> rows harvested from a brand's website. Upserts are keyed by
/// (BrandId, normalized model name) so re-harvesting a brand updates its catalogue in place rather than
/// duplicating models.
/// </summary>
public interface IBrandModelRepository
{
    /// <summary>Inserts a model or overwrites the existing one for the same (brand, normalized name).</summary>
    Task<BrandModel> UpsertAsync(BrandModel model, CancellationToken ct = default);

    /// <summary>All harvested models for a brand, most-recently-refreshed first.</summary>
    Task<IReadOnlyList<BrandModel>> ListByBrandAsync(int brandId, CancellationToken ct = default);

    Task<int> CountForBrandAsync(int brandId, CancellationToken ct = default);

    Task<int> CountAsync(CancellationToken ct = default);
}

public sealed class BrandModelRepository : IBrandModelRepository
{
    private readonly DaleelDbContext _db;

    public BrandModelRepository(DaleelDbContext db) => _db = db;

    public async Task<BrandModel> UpsertAsync(BrandModel model, CancellationToken ct = default)
    {
        var key = string.IsNullOrEmpty(model.ModelKey) ? BrandModel.Normalize(model.ModelName) : model.ModelKey;
        model.ModelKey = key;

        var existing = await _db.BrandModels
            .FirstOrDefaultAsync(m => m.BrandId == model.BrandId && m.ModelKey == key, ct);
        if (existing is not null)
        {
            ApplyUpdates(existing, model);
            await _db.SaveChangesAsync(ct);
            return existing;
        }

        _db.BrandModels.Add(model);
        try
        {
            await _db.SaveChangesAsync(ct);
            return model;
        }
        catch (DbUpdateException)
        {
            // A concurrent insert won the (BrandId, ModelKey) unique-index race: drop ours, reload the
            // committed row, and merge our values onto the winner. Same recovery as BrandRepository.
            _db.Entry(model).State = EntityState.Detached;
            var winner = await _db.BrandModels
                .FirstOrDefaultAsync(m => m.BrandId == model.BrandId && m.ModelKey == key, ct);
            if (winner is null)
            {
                throw;
            }

            ApplyUpdates(winner, model);
            await _db.SaveChangesAsync(ct);
            return winner;
        }
    }

    public async Task<IReadOnlyList<BrandModel>> ListByBrandAsync(int brandId, CancellationToken ct = default) =>
        await _db.BrandModels.AsNoTracking()
            .Where(m => m.BrandId == brandId)
            .OrderByDescending(m => m.LastRefreshed)
            .ToListAsync(ct);

    public Task<int> CountForBrandAsync(int brandId, CancellationToken ct = default) =>
        _db.BrandModels.CountAsync(m => m.BrandId == brandId, ct);

    public Task<int> CountAsync(CancellationToken ct = default) => _db.BrandModels.CountAsync(ct);

    private static void ApplyUpdates(BrandModel existing, BrandModel model)
    {
        existing.ModelName = model.ModelName;
        existing.Category = model.Category;
        existing.SpecsJson = model.SpecsJson;
        // Keep a previously-stored image if the fresh harvest didn't find one (don't blank it out).
        existing.ImageUrl = model.ImageUrl ?? existing.ImageUrl;
        existing.LocalPrice = model.LocalPrice;
        existing.GlobalPrice = model.GlobalPrice;
        existing.Currency = model.Currency;
        existing.IsAvailable = model.IsAvailable;
        existing.SourceUrl = model.SourceUrl;
        existing.LastRefreshed = model.LastRefreshed;
    }
}
