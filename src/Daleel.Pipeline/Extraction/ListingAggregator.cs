using Daleel.Core.Models;

namespace Daleel.Pipeline.Extraction;

/// <summary>
/// Collapses many raw <see cref="ProductListing"/>s (the same model surfaced by several
/// stores) into one <see cref="ProductModel"/> per model, with every source aggregated into
/// price <see cref="PriceOffer"/>s sorted cheapest-first.
/// </summary>
/// <remarks>
/// Pure and deterministic. Grouping reuses <see cref="ListingExtractor.DedupKey"/> so the
/// identity rules (brand+model, else normalized name) match the merge step. Specs and image
/// are unioned across the group so a listing missing one field is backfilled by another.
/// </remarks>
public static class ListingAggregator
{
    /// <summary>Aggregates listings into models, most-sourced (then cheapest) first.</summary>
    public static IReadOnlyList<ProductModel> Aggregate(IEnumerable<ProductListing> listings)
    {
        var groups = listings.GroupBy(ListingExtractor.DedupKey);

        var models = new List<ProductModel>();
        foreach (var group in groups)
        {
            var items = group.ToList();
            var offers = BuildOffers(items);

            // Choose the richest item as the model's canonical identity.
            var canonical = items
                .OrderByDescending(i => i.Specs.Count)
                .ThenByDescending(i => !string.IsNullOrWhiteSpace(i.Model))
                .ThenByDescending(i => i.Name.Length)
                .First();

            models.Add(new ProductModel
            {
                Name = canonical.Name,
                Brand = items.Select(i => i.Brand).FirstOrDefault(b => !string.IsNullOrWhiteSpace(b)),
                Model = items.Select(i => i.Model).FirstOrDefault(m => !string.IsNullOrWhiteSpace(m)),
                ImageUrl = items.Select(i => i.ImageUrl).FirstOrDefault(u => !string.IsNullOrWhiteSpace(u)),
                Specs = MergeSpecs(items),
                Offers = offers
            });
        }

        return models
            .OrderByDescending(m => m.SellerCount)
            .ThenBy(m => m.LowestPrice ?? decimal.MaxValue)
            .ToList();
    }

    private static IReadOnlyList<PriceOffer> BuildOffers(IReadOnlyList<ProductListing> items)
    {
        var offers = items
            .Select(i => new PriceOffer
            {
                Source = string.IsNullOrWhiteSpace(i.Source) ? (i.Seller ?? "Unknown") : i.Source!,
                SourceType = i.SourceType,
                Price = i.Price,
                Currency = i.Currency,
                Url = i.Url,
                Condition = i.Condition,
                Availability = i.Availability,
                Seller = i.Seller,
                OriginalPrice = i.OriginalPrice,
                IsLocal = true, // callers locality-filter before aggregating
                FreeShipping = MentionsFreeShipping(i)
            })
            .OrderBy(o => o.Price ?? decimal.MaxValue)
            .ToList();

        // Flag the cheapest priced offer as LOWEST.
        var cheapest = offers.FirstOrDefault(o => o.Price is not null);
        if (cheapest is not null)
        {
            for (var idx = 0; idx < offers.Count; idx++)
            {
                if (ReferenceEquals(offers[idx], cheapest))
                {
                    offers[idx] = offers[idx] with { IsLowest = true };
                    break;
                }
            }
        }

        return offers;
    }

    private static IReadOnlyDictionary<string, string> MergeSpecs(IReadOnlyList<ProductListing> items)
    {
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            foreach (var (key, value) in item.Specs)
            {
                if (!merged.ContainsKey(key) && !string.IsNullOrWhiteSpace(value))
                {
                    merged[key] = value;
                }
            }
        }

        return merged;
    }

    private static bool MentionsFreeShipping(ProductListing l)
    {
        var text = $"{l.Availability}".ToLowerInvariant();
        return text.Contains("free shipping") || text.Contains("free delivery") ||
               text.Contains("شحن مجاني") || text.Contains("توصيل مجاني");
    }
}
