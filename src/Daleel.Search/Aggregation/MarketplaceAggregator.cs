using Daleel.Core.Arabic;
using Daleel.Core.Models;

namespace Daleel.Search.Aggregation;

/// <summary>Price-comparison summary for a single product across marketplaces.</summary>
public record PriceComparison
{
    public string Product { get; init; } = string.Empty;
    public Money? Lowest { get; init; }
    public Money? Highest { get; init; }
    public Money? Median { get; init; }
    public int StoreCount { get; init; }
    public IReadOnlyList<PricePoint> Offers { get; init; } = Array.Empty<PricePoint>();
}

/// <summary>
/// Combines price points gathered from multiple shopping/marketplace sources (Google
/// Shopping, OpenSooq, scraped stores) into a deduplicated, comparable view.
/// </summary>
/// <remarks>
/// Pure, deterministic logic — no I/O — so it is fully unit-testable. Deduplication keys
/// on (normalized product title + store + amount) so the same listing surfaced by two
/// sources collapses, while genuinely different offers are kept.
/// </remarks>
public static class MarketplaceAggregator
{
    /// <summary>Merges several price-point lists into one, dropping duplicates.</summary>
    public static IReadOnlyList<PricePoint> Merge(params IEnumerable<PricePoint>[] sources)
    {
        var merged = new List<PricePoint>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var source in sources)
        {
            foreach (var p in source)
            {
                var key = DedupKey(p);
                if (seen.Add(key))
                {
                    merged.Add(p);
                }
            }
        }

        return merged;
    }

    /// <summary>
    /// Builds a per-product price comparison. Only offers sharing a currency are compared
    /// together (Daleel does not convert currencies), so figures stay apples-to-apples.
    /// </summary>
    public static PriceComparison Compare(string product, IEnumerable<PricePoint> offers)
    {
        // Keep only real, positive prices and group by currency, then take the most
        // populous currency group so the comparison is internally consistent.
        var priced = offers
            .Where(o => o.Price.Amount > 0)
            .GroupBy(o => o.Price.Currency)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault();

        if (priced is null)
        {
            return new PriceComparison { Product = product };
        }

        var ordered = priced.OrderBy(o => o.Price.Amount).ToList();
        var amounts = ordered.Select(o => o.Price.Amount).ToList();

        return new PriceComparison
        {
            Product = product,
            Lowest = ordered[0].Price,
            Highest = ordered[^1].Price,
            Median = new Money(MedianOf(amounts), priced.Key),
            StoreCount = ordered.Select(o => o.Store ?? o.Url ?? o.Product).Distinct().Count(),
            Offers = ordered
        };
    }

    private static decimal MedianOf(IReadOnlyList<decimal> sortedAmounts)
    {
        var n = sortedAmounts.Count;
        if (n == 0) return 0;
        return n % 2 == 1
            ? sortedAmounts[n / 2]
            : (sortedAmounts[(n / 2) - 1] + sortedAmounts[n / 2]) / 2m;
    }

    private static string DedupKey(PricePoint p)
    {
        var title = ArabicNormalizer.Normalize(p.Product);
        var store = (p.Store ?? string.Empty).Trim().ToLowerInvariant();
        return $"{title}|{store}|{p.Price.Amount}|{p.Price.Currency}";
    }
}
