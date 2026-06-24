using System.Text.RegularExpressions;
using Daleel.Core.Llm;

namespace Daleel.Core.Intelligence;

/// <summary>
/// How a single search result should be treated when building a product report. A result
/// is classified along this one axis: is it a concrete product to buy, a place that sells
/// products, a brand's own pages, editorial, or a marketplace category to browse.
/// </summary>
public enum ResultType
{
    /// <summary>Couldn't be classified with confidence.</summary>
    Unknown,

    /// <summary>A specific product, usually with a price (e.g. "Samsung AR24 - 450 JOD").</summary>
    ProductListing,

    /// <summary>A brand's own catalog/product page (e.g. samsung.com/jo air-conditioners).</summary>
    BrandPage,

    /// <summary>A retailer selling many products (e.g. Carrefour electronics section).</summary>
    StorePage,

    /// <summary>A comparison/review/buying-guide article (e.g. "Best ACs in Jordan 2024").</summary>
    ReviewArticle,

    /// <summary>A marketplace category/listing page (e.g. OpenSooq Air Conditioners).</summary>
    Marketplace
}

/// <summary>
/// Classifies a raw search hit (URL + title + snippet) into a <see cref="ResultType"/> so
/// the agent can route it to the right bucket of a product report — actual listings vs.
/// stores to visit vs. brand pages vs. reviews.
/// </summary>
/// <remarks>
/// The deterministic <see cref="Classify"/> is the workhorse and is fully unit-testable:
/// it layers known-host patterns, then title/snippet keywords, then a price-pattern
/// heuristic. <see cref="ClassifyAsync"/> only consults an <see cref="ILlmClient"/> when
/// the deterministic pass yields <see cref="ResultType.Unknown"/>, so most calls cost
/// nothing.
/// </remarks>
public static class ResultClassifier
{
    // Hosts whose pages are brand catalogs. Keyed on a host substring.
    private static readonly string[] BrandHosts =
    {
        "samsung.com", "lg.com", "lge.com", "sharpme", "midea", "gree", "haier",
        "panasonic.com", "daikin", "carrier", "hisense", "tcl.com", "toshiba"
    };

    // Marketplaces / classifieds: a bare category page is Marketplace, a single item is ProductListing.
    private static readonly string[] MarketplaceHosts =
    {
        "opensooq.com", "dubizzle.com", "haraj.com", "olx.com", "amazon.", "noon.com",
        "ebay.com", "souq.com", "jumia.com", "aliexpress.com", "craigslist.org"
    };

    // Multi-product retailers/stores.
    private static readonly string[] StoreHosts =
    {
        "carrefour", "xcite.com", "safeway", "leaders.jo", "smartbuy", "bestbuy.com",
        "homedepot.com", "lulu", "sharafdg", "jarir.com", "extra.com", "btc.com.jo"
    };

    // Title/snippet keywords that mark editorial content (English + Arabic).
    private static readonly string[] ReviewKeywords =
    {
        "best ", "top ", " review", "reviews", "vs ", "comparison", "compare",
        "buying guide", "guide to", "أفضل", "مراجعة", "مقارنة", "دليل شراء", "أرخص"
    };

    // Title/snippet keywords that mark a marketplace category/listing page.
    private static readonly string[] MarketplaceKeywords =
    {
        "for sale", "listings", "classifieds", "ads", "للبيع", "إعلانات", "مستعمل"
    };

    // Currency / price tokens (English + Arabic + Gulf) that signal a priced listing.
    private static readonly Regex PricePattern = new(
        @"(\d[\d,.]*\s*(jod|jd|sar|aed|egp|usd|دينار|د\.?ا|ريال|درهم|جنيه|\$|ج\.?م)|" +
        @"(jod|jd|sar|aed|egp|usd|دينار|ريال|درهم|جنيه|\$)\s*\d[\d,.]*|price\s*[:=]|السعر)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Classifies a result deterministically. Returns <see cref="ResultType.Unknown"/>
    /// when no rule fires (the caller may then fall back to the LLM).
    /// </summary>
    public static ResultType Classify(string? url, string? title, string? snippet)
    {
        var host = HostOf(url);
        var text = $"{title} {snippet}".ToLowerInvariant();
        var hasPrice = !string.IsNullOrEmpty(text) && PricePattern.IsMatch(text);
        var path = PathOf(url);

        // 1) Brand sites are unambiguous regardless of price.
        if (host.Length > 0 && BrandHosts.Any(h => host.Contains(h, StringComparison.Ordinal)))
        {
            return ResultType.BrandPage;
        }

        // 2) Editorial wins over host when the title clearly reads as a guide/review.
        if (ContainsAny(text, ReviewKeywords))
        {
            return ResultType.ReviewArticle;
        }

        // 3) Marketplaces: a deep item path or a price ⇒ a single listing; otherwise the
        //    category page itself.
        if (host.Length > 0 && MarketplaceHosts.Any(h => host.Contains(h, StringComparison.Ordinal)))
        {
            return hasPrice || LooksLikeItemPath(path) ? ResultType.ProductListing : ResultType.Marketplace;
        }

        // 4) Known multi-product retailers: a priced page is a listing, else the store.
        if (host.Length > 0 && StoreHosts.Any(h => host.Contains(h, StringComparison.Ordinal)))
        {
            return hasPrice ? ResultType.ProductListing : ResultType.StorePage;
        }

        // 5) Generic signals from the text.
        if (ContainsAny(text, MarketplaceKeywords))
        {
            return hasPrice ? ResultType.ProductListing : ResultType.Marketplace;
        }

        // 6) Any other page that quotes a price is treated as a listing.
        if (hasPrice)
        {
            return ResultType.ProductListing;
        }

        // 7) A bare commercial domain (e.g. "{brand}.com") with no other signal reads as a brand page.
        if (IsBareDomainHomepage(path) && host.Length > 0)
        {
            return ResultType.BrandPage;
        }

        return ResultType.Unknown;
    }

    /// <summary>
    /// Classifies a result, consulting the LLM only when the deterministic pass is
    /// inconclusive. The LLM is asked for a single label; anything it returns that doesn't
    /// map to a known type collapses to <see cref="ResultType.Unknown"/>.
    /// </summary>
    public static async Task<ResultType> ClassifyAsync(
        string? url, string? title, string? snippet,
        ILlmClient? llm, CancellationToken cancellationToken = default)
    {
        var deterministic = Classify(url, title, snippet);
        if (deterministic != ResultType.Unknown || llm is null)
        {
            return deterministic;
        }

        var system = "You label web search results for a shopping assistant. Reply with EXACTLY one " +
                     "of these words and nothing else: ProductListing, BrandPage, StorePage, ReviewArticle, Marketplace.";
        var prompt = $"URL: {url}\nTitle: {title}\nSnippet: {snippet}\nLabel:";

        try
        {
            var reply = await llm.CompleteTextAsync(system, prompt, cancellationToken).ConfigureAwait(false);
            return Parse(reply);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ResultType.Unknown;
        }
    }

    /// <summary>Parses an LLM/textual label into a <see cref="ResultType"/>.</summary>
    public static ResultType Parse(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return ResultType.Unknown;
        }

        var s = label.Trim().ToLowerInvariant();
        if (s.Contains("productlisting") || s.Contains("product listing")) return ResultType.ProductListing;
        if (s.Contains("brandpage") || s.Contains("brand")) return ResultType.BrandPage;
        if (s.Contains("storepage") || s.Contains("store")) return ResultType.StorePage;
        if (s.Contains("reviewarticle") || s.Contains("review")) return ResultType.ReviewArticle;
        if (s.Contains("marketplace")) return ResultType.Marketplace;
        return ResultType.Unknown;
    }

    private static bool ContainsAny(string haystack, string[] needles) =>
        needles.Any(n => haystack.Contains(n, StringComparison.Ordinal));

    private static string HostOf(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        return Uri.TryCreate(url, UriKind.Absolute, out var u)
            ? u.Host.ToLowerInvariant()
            : string.Empty;
    }

    private static string PathOf(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        return Uri.TryCreate(url, UriKind.Absolute, out var u)
            ? u.AbsolutePath.ToLowerInvariant()
            : string.Empty;
    }

    // Marketplace item pages tend to carry a numeric id segment (e.g. /listing/12345678).
    private static bool LooksLikeItemPath(string path) =>
        Regex.IsMatch(path, @"/\d{5,}") ||
        path.Contains("/item/", StringComparison.Ordinal) ||
        path.Contains("/listing/", StringComparison.Ordinal) ||
        path.Contains("/dp/", StringComparison.Ordinal) ||
        path.Contains("/product/", StringComparison.Ordinal) ||
        path.Contains("/p/", StringComparison.Ordinal);

    private static bool IsBareDomainHomepage(string path) =>
        path.Length == 0 || path == "/" || path.Count(c => c == '/') <= 1;
}
