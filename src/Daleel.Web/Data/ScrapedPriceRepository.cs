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
        // then keep the first row per store. The collapse is in memory because SQLite can't translate a
        // GROUP BY + "row with max timestamp" into a single query without window functions EF won't emit.
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

    public Task<int> CountAsync(CancellationToken ct = default) => _db.ScrapedPrices.CountAsync(ct);
}
