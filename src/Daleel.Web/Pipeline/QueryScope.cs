using Daleel.Core.Geo;

namespace Daleel.Web.Pipeline;

/// <summary>
/// The market-scope-aware query cleaner shared by every path that types a shopper's query into a STORE's own
/// on-site search box (the enrichment single-page harvest AND the LLM site crawl). Store search engines
/// AND-match every term against product titles, so geography words and filler ("electric kettle IN AMMAN",
/// "diapers Jordan") match no product and make the store answer with its no-results page — a store that
/// actually stocks the item then yields zero. Stripping market scope + stopwords down to the product tokens is
/// the difference between a hit and an empty grid. Kept in one place so the two crawlers can never drift apart.
/// </summary>
public static class QueryScope
{
    private static readonly char[] Separators = " \t\r\n-_/\\|,.()[]{}،:;\"'".ToCharArray();

    // Filler that is market/intent scope, never product identity ("best AC deals near me" → "AC").
    private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "in", "the", "a", "an", "for", "of", "and", "or", "best", "price", "prices",
        "buy", "cheap", "deal", "deals", "near", "me", "shop", "store", "stores", "online"
    };

    /// <summary>
    /// The product-only tokens of <paramref name="query"/> with market scope and stopwords removed: geography
    /// words (the geo key, its country, and its centre city — "jordan", "amman") are stripped because they never
    /// appear in a product title. Order-preserving and case-insensitively de-duplicated; two-letter product
    /// nouns ("AC", "TV") are kept.
    /// </summary>
    public static IReadOnlyList<string> SignificantTokens(string? query, string? geo = null)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<string>();
        }

        var geoTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(geo))
        {
            geoTokens.UnionWith(Tokenize(geo!));
            var profile = GeoProfiles.ResolveOrDefault(geo);
            geoTokens.UnionWith(Tokenize(profile.Country));
            geoTokens.UnionWith(Tokenize(profile.CenterCity));
        }

        return Tokenize(query!)
            .Where(t => t.Length >= 2 && !Stopwords.Contains(t) && !geoTokens.Contains(t))
            .ToList();
    }

    /// <summary>
    /// The term to type into a store's search box for <paramref name="query"/> in market <paramref name="geo"/>:
    /// the significant product tokens joined by a space. Falls back to the trimmed raw query when cleaning would
    /// leave nothing (a query that is ALL geo/filler is better searched raw than not at all).
    /// </summary>
    public static string StoreSearchTerm(string? query, string? geo = null)
    {
        var tokens = SignificantTokens(query, geo);
        return tokens.Count > 0 ? string.Join(' ', tokens) : (query ?? string.Empty).Trim();
    }

    /// <summary>Order-preserving, case-insensitively de-duplicated 2+char tokens.</summary>
    private static List<string> Tokenize(string? text)
    {
        var ordered = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text))
        {
            return ordered;
        }

        foreach (var t in text.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (t.Length >= 2 && seen.Add(t))
            {
                ordered.Add(t);
            }
        }

        return ordered;
    }
}
