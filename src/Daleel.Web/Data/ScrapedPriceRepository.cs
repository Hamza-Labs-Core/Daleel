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
    /// Like <see cref="LatestForStoreAsync"/> but restricted to observations captured at or after
    /// <paramref name="since"/>. The enrichment units use this to re-attach prices an earlier pass
    /// (e.g. a drained edge scrape) already persisted, without re-spending a crawl. The default
    /// composes over <see cref="LatestForStoreAsync"/> so existing implementations keep compiling;
    /// <see cref="ScrapedPriceRepository"/> overrides it with an indexed range read.
    /// </summary>
    async Task<IReadOnlyList<ScrapedPrice>> ListRecentByStoreAsync(
        string storeName, DateTimeOffset since, CancellationToken ct = default) =>
        (await LatestForStoreAsync(storeName, ct)).Where(p => p.ScrapedAt >= since).ToList();

    /// <summary>
    /// Recent raw observations for a product across all stores, newest first (capped at
    /// <paramref name="max"/>). The product page uses this to show the observed price range over time.
    /// </summary>
    Task<IReadOnlyList<ScrapedPrice>> HistoryForProductAsync(string productKey, int max = 500, CancellationToken ct = default);

    Task<int> CountAsync(CancellationToken ct = default);
}

public sealed class ScrapedPriceRepository : IScrapedPriceRepository
{
    /// <summary>
    /// Upper bound on how many recent observations <see cref="LatestForProductAsync"/> pulls into memory
    /// before collapsing to one row per store. The table is append-only and never pruned, so without this
    /// the read degrades to materializing the entire per-key history. Newest-first ordering means the most
    /// recent price for every active store is comfortably inside this window.
    /// </summary>
    private const int MaxHistoryRows = 500;

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
        // then keep the first row per store. The collapse is done in memory (the windows are small) so the
        // GROUP BY + "row with max timestamp" stays a pure, tested function rather than relying on window
        // functions EF won't emit.
        var rows = await _db.ScrapedPrices.AsNoTracking()
            .Where(p => p.ProductKey == productKey)
            .OrderByDescending(p => p.ScrapedAt)
            .Take(MaxHistoryRows)
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
        // (lower() is provider-translatable). Then collapse the store's history to the current price per
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

    public async Task<IReadOnlyList<ScrapedPrice>> ListRecentByStoreAsync(
        string storeName, DateTimeOffset since, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(storeName))
        {
            return Array.Empty<ScrapedPrice>();
        }

        // Same case-insensitive store match as LatestForStoreAsync, cut to the recency window first
        // (ScrapedAt's Unix-ms encoding keeps the range filter translatable and indexed) so the
        // append-only table's depth never bleeds into this read, then the same per-product collapse.
        var lowered = storeName.Trim().ToLowerInvariant();
        var rows = await _db.ScrapedPrices.AsNoTracking()
            .Where(p => p.StoreName.ToLower() == lowered && p.ScrapedAt >= since)
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
