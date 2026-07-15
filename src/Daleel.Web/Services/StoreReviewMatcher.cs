using Daleel.Core.Models;

namespace Daleel.Web.Services;

/// <summary>
/// Associates a product card with the reviews of the stores selling it. There is no shared id
/// between a <see cref="PriceOffer"/> and a <see cref="StoreInfo"/>, so this is a deliberate
/// best-effort fuzzy join: normalized store-name equality (offer Source/Seller vs store Name),
/// then website-domain equality (offer Url host vs store Url host). A store that matches nothing
/// simply contributes no reviews — misses are expected and never an error.
/// </summary>
public static class StoreReviewMatcher
{
    /// <summary>The reviews of every store that plausibly sells this model (deduped store-level).</summary>
    public static IReadOnlyList<StoreReview> ReviewsFor(ProductModel model, IReadOnlyList<StoreInfo> stores)
    {
        if (model.Offers.Count == 0 || stores.Count == 0)
        {
            return Array.Empty<StoreReview>();
        }

        var offerNames = model.Offers
            .SelectMany(o => new[] { o.Source, o.Seller })
            .Select(NormalizeName)
            .Where(n => n.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        var offerHosts = model.Offers
            .Select(o => HostOf(o.Url))
            .Where(h => h is not null)
            .Select(h => h!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var reviews = new List<StoreReview>();
        foreach (var store in stores)
        {
            if (store.Reviews.Count == 0)
            {
                continue;
            }

            var byName = offerNames.Contains(NormalizeName(store.Name));
            var byHost = HostOf(store.Url) is { } host && offerHosts.Contains(host);
            if (byName || byHost)
            {
                reviews.AddRange(store.Reviews);
            }
        }
        return reviews;
    }

    /// <summary>Case-fold and strip non-alphanumerics: "  SmartBuy " ≡ "smartbuy" ≡ "Smart-Buy".</summary>
    private static string NormalizeName(string? name) =>
        name is null ? string.Empty : new string(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>The URL's host with any leading "www." stripped, else null.</summary>
    private static string? HostOf(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u)
            ? (u.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? u.Host[4..] : u.Host)
            : null;
}
