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
            // A concurrent insert won the (BrandId, ModelKey) unique-index race. Reset the WHOLE change
            // tracker (not just our failed entity): this upsert can run on the shared request-scoped
            // DbContext during brand-catalogue harvesting, so a single failed SaveChanges could otherwise
            // leave a poisoned tracker that makes the next model's SaveChanges re-throw and silently drop
            // models. Then reload the committed row and merge our values onto the winner. Same recovery as
            // BrandRepository.
            _db.ChangeTracker.Clear();
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
        // Null-coalesce the optional enrichment fields: structured LLM/Context.dev extraction is flaky,
        // so a partial re-harvest that momentarily omits specs/prices/category must NOT blank out
        // previously-good values. "Absent this harvest" means "unknown", not "deleted". Only overwrite a
        // field when the fresh harvest actually carries a value for it.
        if (!string.IsNullOrWhiteSpace(model.ModelName))
        {
            existing.ModelName = model.ModelName;
        }

        existing.Category = model.Category ?? existing.Category;
        existing.SpecsJson = model.SpecsJson ?? existing.SpecsJson;
        existing.ImageUrl = model.ImageUrl ?? existing.ImageUrl;
        existing.LocalPrice = model.LocalPrice ?? existing.LocalPrice;
        existing.GlobalPrice = model.GlobalPrice ?? existing.GlobalPrice;
        existing.Currency = model.Currency ?? existing.Currency;
        existing.SourceUrl = model.SourceUrl ?? existing.SourceUrl;
        // IsAvailable and LastRefreshed are always current-harvest facts (no "unknown" state).
        existing.IsAvailable = model.IsAvailable;
        existing.LastRefreshed = model.LastRefreshed;
    }
}
