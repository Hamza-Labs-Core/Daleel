using Microsoft.EntityFrameworkCore;

namespace Daleel.Web.Data;

/// <summary>
/// Persistence for the append-only <see cref="ScrapedPrice"/> time series. Writes are pure inserts
/// (every scrape is a new observation), and the hot read is "the latest price per store for a model".
/// </summary>
public interface IScrapedPriceRepository
{
    /// <summary>Records one price observation.</summary>
    Task AddAsync(ScrapedPrice price, CancellationToken ct = default);

    /// <summary>Records a batch of price observations in a single round-trip.</summary>
    Task AddRangeAsync(IReadOnlyCollection<ScrapedPrice> prices, CancellationToken ct = default);

    /// <summary>
    /// The most recent observation per store for a normalized product key, newest first. Collapses the
    /// history to one current price per retailer (the price comparison the UI renders).
    /// </summary>
    Task<IReadOnlyList<ScrapedPrice>> LatestForProductAsync(string productKey, CancellationToken ct = default);

    /// <summary>
    /// The most recent observation per product at a given store (matched case-insensitively), newest
    /// first. Drives the "products carried with prices" list on a store's page.
    /// </summary>
    Task<IReadOnlyList<ScrapedPrice>> LatestForStoreAsync(string storeName, CancellationToken ct = default);

    /// <summary>
    /// Recent raw observations for a product across all stores, newest first (capped at
    /// <paramref name="max"/>). The product page uses this to show the observed price range over time.
    /// </summary>
    Task<IReadOnlyList<ScrapedPrice>> HistoryForProductAsync(string productKey, int max = 500, CancellationToken ct = default);

    Task<int> CountAsync(CancellationToken ct = default);
}

public sealed class ScrapedPriceRepository : IScrapedPriceRepository
{
    private readonly DaleelDbContext _db;

    public ScrapedPriceRepository(DaleelDbContext db) => _db = db;

    public async Task AddAsync(ScrapedPrice price, CancellationToken ct = default)
    {
        _db.ScrapedPrices.Add(price);
        await _db.SaveChangesAsync(ct);
    }

    public async Task AddRangeAsync(IReadOnlyCollection<ScrapedPrice> prices, CancellationToken ct = default)
    {
        if (prices.Count == 0)
        {
            return;
        }

        _db.ScrapedPrices.AddRange(prices);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<ScrapedPrice>> LatestForProductAsync(
        string productKey, CancellationToken ct = default)
    {
        // Pull the model's history newest-first (a translatable WHERE + ORDER BY on the Unix-ms column),
        // then keep the first row per store. The collapse is in memory because SQLite can't translate a
        // GROUP BY + "row with max timestamp" into a single query without window functions EF won't emit.
        var rows = await _db.ScrapedPrices.AsNoTracking()
            .Where(p => p.ProductKey == productKey)
            .OrderByDescending(p => p.ScrapedAt)
            .ToListAsync(ct);

        return rows
            .GroupBy(p => p.StoreName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    public async Task<IReadOnlyList<ScrapedPrice>> LatestForStoreAsync(
        string storeName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(storeName))
        {
            return Array.Empty<ScrapedPrice>();
        }

        // Scraped store names and a saved Store's name can differ in casing, so match case-insensitively
        // (lower() is SQLite-translatable). Then collapse the store's history to the current price per
        // product, newest observation winning — the same per-key collapse LatestForProductAsync does.
        var lowered = storeName.Trim().ToLowerInvariant();
        var rows = await _db.ScrapedPrices.AsNoTracking()
            .Where(p => p.StoreName.ToLower() == lowered)
            .OrderByDescending(p => p.ScrapedAt)
            .ToListAsync(ct);

        return rows
            .GroupBy(p => p.ProductKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    public async Task<IReadOnlyList<ScrapedPrice>> HistoryForProductAsync(
        string productKey, int max = 500, CancellationToken ct = default) =>
        string.IsNullOrWhiteSpace(productKey)
            ? Array.Empty<ScrapedPrice>()
            : await _db.ScrapedPrices.AsNoTracking()
                .Where(p => p.ProductKey == productKey)
                .OrderByDescending(p => p.ScrapedAt)
                .Take(max)
                .ToListAsync(ct);

    public Task<int> CountAsync(CancellationToken ct = default) => _db.ScrapedPrices.CountAsync(ct);
}
