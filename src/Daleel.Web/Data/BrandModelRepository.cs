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

    /// <summary>The model for a (brand, normalized name) pair, or null if not harvested yet (no tracking).</summary>
    Task<BrandModel?> GetByBrandAndKeyAsync(int brandId, string modelKey, CancellationToken ct = default);

    /// <summary>A single harvested model by its database id (with its owning brand loaded), or null.</summary>
    Task<BrandModel?> GetByIdAsync(int id, CancellationToken ct = default);

    /// <summary>
    /// Persists the canonical (merged-and-cleaned) spec sheet onto an existing model — the only write
    /// the spec pipeline makes after a model has been identified. Best-effort: a missing id is a no-op.
    /// </summary>
    Task SaveFinalSpecsAsync(int id, string? finalSpecsJson, string? finalSpecsR2Url, CancellationToken ct = default);

    /// <summary>
    /// Finds the harvested model whose identity normalizes to <paramref name="productKey"/> — the same
    /// normalized "brand model" key <see cref="ProductProfile"/>/<see cref="ScrapedPrice"/> are stored
    /// under — so a product detail page can resolve a catalogue row from the key it already has for
    /// prices/profile. Returns null when no model matches.
    /// </summary>
    Task<BrandModel?> FindByProductKeyAsync(string productKey, CancellationToken ct = default);

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

    public Task<BrandModel?> GetByBrandAndKeyAsync(int brandId, string modelKey, CancellationToken ct = default) =>
        _db.BrandModels.AsNoTracking()
            .FirstOrDefaultAsync(m => m.BrandId == brandId && m.ModelKey == modelKey, ct);

    public Task<BrandModel?> GetByIdAsync(int id, CancellationToken ct = default) =>
        _db.BrandModels.AsNoTracking()
            .Include(m => m.Brand)
            .FirstOrDefaultAsync(m => m.Id == id, ct);

    public async Task SaveFinalSpecsAsync(int id, string? finalSpecsJson, string? finalSpecsR2Url, CancellationToken ct = default)
    {
        var existing = await _db.BrandModels.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (existing is null)
        {
            return; // the model was deleted between identification and merge — nothing to write
        }

        existing.FinalSpecsJson = finalSpecsJson ?? existing.FinalSpecsJson;
        existing.FinalSpecsR2Url = finalSpecsR2Url ?? existing.FinalSpecsR2Url;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<BrandModel?> FindByProductKeyAsync(string productKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(productKey))
        {
            return null;
        }

        // BrandModel is keyed per-brand by (BrandId, ModelKey), not by the cross-table normalized
        // "brand model" key that ProductProfile/ScrapedPrice share — and EF can't translate that
        // normalization into SQL. The harvested catalogue is small (a handful of brands), so we load it
        // and match in memory: a model matches when its brand+model (or model alone) normalizes to the
        // same key the price/profile rows are stored under. Newest harvest wins on ties.
        var models = await _db.BrandModels.AsNoTracking().Include(m => m.Brand).ToListAsync(ct);
        return models
            .Where(m => ProductProfile.KeyFor(m.Brand?.Name, m.ModelName, m.ModelName) == productKey
                        || ProductProfile.Normalize(m.ModelName) == productKey)
            .OrderByDescending(m => m.LastRefreshed)
            .FirstOrDefault();
    }

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
        // Site-hierarchy attribution moves as a PAIR: only a harvest that knows its level may
        // re-attribute the row (level + country together — coalescing them independently could
        // pair "global" with a stale local country). Level-aware harvests run global→local, so on
        // a (BrandId, ModelKey) collision the local attribution lands last and wins.
        if (model.SiteLevel is not null)
        {
            existing.SiteLevel = model.SiteLevel;
            existing.SiteCountry = model.SiteCountry;
        }

        // IsAvailable and LastRefreshed are always current-harvest facts (no "unknown" state).
        existing.IsAvailable = model.IsAvailable;
        existing.LastRefreshed = model.LastRefreshed;

        // Smart-identification fields. The canonical spec sheet only ever moves forward (a re-harvest
        // that hasn't re-run the merge carries no FinalSpecs, and must not erase a good one). Image and
        // alias lists are UNIONED, not replaced: each regional crawl discovers a different subset, and we
        // want their cumulative knowledge so a store photo can match against every shot we've ever seen.
        existing.FinalSpecsJson = model.FinalSpecsJson ?? existing.FinalSpecsJson;
        existing.FinalSpecsR2Url = model.FinalSpecsR2Url ?? existing.FinalSpecsR2Url;
        existing.ImageR2Urls = UnionPreserveOrder(existing.ImageR2Urls, model.ImageR2Urls);
        existing.RegionalAliases = UnionPreserveOrder(existing.RegionalAliases, model.RegionalAliases);
        // DiscoveredAt is a "first seen" fact: keep the earliest non-default value, never overwrite it
        // with a later harvest's timestamp.
        if (existing.DiscoveredAt == default)
        {
            existing.DiscoveredAt = model.DiscoveredAt;
        }

        // Sticky: a re-harvest that simply doesn't list a model can't observe its discontinuation, so never
        // clear a previously-set flag — only let a harvest turn it on.
        existing.IsDiscontinued = existing.IsDiscontinued || model.IsDiscontinued;
    }

    /// <summary>Merges two string lists, case-insensitively de-duplicated, preserving first-seen order.</summary>
    private static List<string> UnionPreserveOrder(IEnumerable<string>? first, IEnumerable<string>? second)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var s in (first ?? Enumerable.Empty<string>()).Concat(second ?? Enumerable.Empty<string>()))
        {
            if (!string.IsNullOrWhiteSpace(s) && seen.Add(s.Trim()))
            {
                result.Add(s.Trim());
            }
        }

        return result;
    }
}
