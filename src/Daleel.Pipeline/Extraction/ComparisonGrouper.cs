using Daleel.Core.Models;

namespace Daleel.Pipeline.Extraction;

/// <summary>
/// Pre-groups priced product listings into budget / mid-range / premium tiers so the UI
/// can offer ready-made comparisons without the user hand-picking items.
/// </summary>
/// <remarks>
/// Pure and deterministic. Only listings sharing the dominant currency are tiered together
/// (Daleel never converts currencies), and price terciles define the cut points so the
/// tiers adapt to whatever the market's actual price spread is.
/// </remarks>
public static class ComparisonGrouper
{
    /// <summary>
    /// Splits listings into up to three price tiers. Falls back to a single "All options"
    /// group when there aren't enough priced listings to form meaningful tiers.
    /// </summary>
    public static IReadOnlyList<ComparisonGroup> Group(
        IEnumerable<ProductListing> listings, int maxPerGroup = 4)
    {
        // Keep only positively-priced listings in the dominant currency.
        var priced = listings
            .Where(l => l.Price is > 0 && !string.IsNullOrWhiteSpace(l.Currency))
            .GroupBy(l => l.Currency!)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault();

        if (priced is null)
        {
            return Array.Empty<ComparisonGroup>();
        }

        var sorted = priced.OrderBy(l => l.Price!.Value).ToList();

        // Too few to tier meaningfully — present them as one comparable set.
        if (sorted.Count < 3)
        {
            return new[] { BuildGroup("All options", sorted, maxPerGroup) };
        }

        var third = sorted.Count / 3;
        var budget = sorted.Take(third).ToList();
        var premium = sorted.Skip(sorted.Count - third).ToList();
        var mid = sorted.Skip(third).Take(sorted.Count - 2 * third).ToList();

        return new[]
        {
            BuildGroup("Budget", budget, maxPerGroup),
            BuildGroup("Mid-range", mid, maxPerGroup),
            BuildGroup("Premium", premium, maxPerGroup)
        }
        .Where(g => g.Items.Count > 0)
        .ToList();
    }

    private static ComparisonGroup BuildGroup(string category, List<ProductListing> tier, int maxPerGroup)
    {
        return new ComparisonGroup
        {
            Category = category,
            Items = tier.Take(maxPerGroup).ToList(),
            Recommendation = Recommend(tier)
        };
    }

    /// <summary>
    /// Picks a value recommendation within a tier: an active deal first, otherwise the
    /// cheapest option.
    /// </summary>
    private static string? Recommend(IReadOnlyList<ProductListing> tier)
    {
        if (tier.Count == 0)
        {
            return null;
        }

        var deal = tier.FirstOrDefault(l => l.IsDeal);
        if (deal is not null)
        {
            return $"Best deal: {Label(deal)} — discounted from {deal.OriginalPrice:0.##} to {deal.Price:0.##} {deal.Currency}.";
        }

        var cheapest = tier[0]; // tier is price-ascending
        return $"Best value: {Label(cheapest)} at {cheapest.Price:0.##} {cheapest.Currency}.";
    }

    private static string Label(ProductListing l)
    {
        if (!string.IsNullOrWhiteSpace(l.Name))
        {
            return l.Name;
        }

        var brandModel = string.Join(" ", new[] { l.Brand, l.Model }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return string.IsNullOrWhiteSpace(brandModel) ? "this option" : brandModel;
    }
}
