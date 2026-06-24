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

    /// <summary>A retailer selling many products (e.g. an electronics store's AC section).</summary>
    StorePage,

    /// <summary>A comparison/review/buying-guide article (e.g. "Best ACs in Jordan 2024").</summary>
    ReviewArticle,

    /// <summary>A marketplace category/listing page (e.g. a classifieds Air-Conditioners section).</summary>
    Marketplace
}

/// <summary>
/// Classifies a raw search hit (URL + title + snippet) into a <see cref="ResultType"/> so
/// the agent can route it to the right bucket of a product report.
/// </summary>
/// <remarks>
/// Deliberately <strong>geo-agnostic</strong>: it knows nothing about which specific stores
/// or marketplaces exist in any country. It reasons only from generic, structural signals —
/// a quoted price, an item-style URL path, classifieds/listing keywords, a shop/category
/// path — and falls back to "brand page" for an otherwise-plain commercial URL. Because the
/// search engine already surfaces the right local sites for a geo, the classifier never
/// needs a hardcoded source list. <see cref="ClassifyAsync"/> consults an
/// <see cref="ILlmClient"/> only when there is no URL to reason about.
/// </remarks>
public static class ResultClassifier
{
    // Title/snippet keywords that mark editorial content (English + Arabic).
    private static readonly string[] ReviewKeywords =
    {
        "best ", "top ", " review", "reviews", " vs ", "comparison", "compare",
        "buying guide", "buyer's guide", "guide to", "أفضل", "مراجعة", "مقارنة", "دليل شراء"
    };

    // Title/snippet keywords that mark a classifieds / marketplace category page.
    private static readonly string[] MarketplaceKeywords =
    {
        "for sale", "classifieds", "listings", "ads in", "used ", "للبيع", "إعلانات", "مستعمل", "حراج"
    };

    // Generic URL path fragments that signal a single product page.
    private static readonly string[] ItemPathFragments =
    {
        "/dp/", "/product/", "/products/", "/item/", "/listing/", "/p/", "/ad/", "/offer/"
    };

    // Generic URL path fragments that signal a multi-product store / category page.
    private static readonly string[] StorePathFragments =
    {
        "/category/", "/categories/", "/shop/", "/store/", "/collections/", "/c/", "/catalog/", "/department/"
    };

    // Currency / price tokens (English + Arabic + Gulf) that signal a priced listing.
    private static readonly Regex PricePattern = new(
        @"(\d[\d,.]*\s*(jod|jd|sar|aed|egp|usd|دينار|د\.?ا|ريال|درهم|جنيه|\$|ج\.?م)|" +
        @"(jod|jd|sar|aed|egp|usd|دينار|ريال|درهم|جنيه|\$)\s*\d[\d,.]*|price\s*[:=]|السعر)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Classifies a result deterministically from generic signals. Returns
    /// <see cref="ResultType.Unknown"/> only when there is no URL at all.
    /// </summary>
    public static ResultType Classify(string? url, string? title, string? snippet)
    {
        var text = $"{title} {snippet}".ToLowerInvariant();
        var hasPrice = !string.IsNullOrWhiteSpace(text) && PricePattern.IsMatch(text);
        var path = PathOf(url);

        // 1) Editorial first — a guide/review title trumps everything.
        if (ContainsAny(text, ReviewKeywords))
        {
            return ResultType.ReviewArticle;
        }

        // 2) A quoted price means a concrete, buyable listing.
        if (hasPrice)
        {
            return ResultType.ProductListing;
        }

        if (string.IsNullOrEmpty(path) && string.IsNullOrEmpty(HostOf(url)))
        {
            // No URL to reason about — leave for the LLM fallback.
            return ResultType.Unknown;
        }

        // 3) An item-style path (numeric id or a /product//item//dp/ fragment) is a single listing.
        if (LooksLikeItemPath(path))
        {
            return ResultType.ProductListing;
        }

        // 4) Classifieds / "for sale" wording without a price is a marketplace category page.
        if (ContainsAny(text, MarketplaceKeywords))
        {
            return ResultType.Marketplace;
        }

        // 5) A shop/category path is a multi-product store page.
        if (ContainsAny(path, StorePathFragments))
        {
            return ResultType.StorePage;
        }

        // 6) Fallback: a plain commercial URL with no other signal reads as a brand page.
        return ResultType.BrandPage;
    }

    /// <summary>
    /// Classifies a result, consulting the LLM only when there is no URL to reason about.
    /// Anything the LLM returns that doesn't map to a known type collapses to
    /// <see cref="ResultType.Unknown"/>.
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
        return Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host.ToLowerInvariant() : string.Empty;
    }

    private static string PathOf(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        return Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.AbsolutePath.ToLowerInvariant() : string.Empty;
    }

    // Item pages tend to carry a numeric id segment (e.g. /listing/12345678) or a known item fragment.
    private static bool LooksLikeItemPath(string path) =>
        Regex.IsMatch(path, @"/\d{5,}") || ContainsAny(path, ItemPathFragments);
}
