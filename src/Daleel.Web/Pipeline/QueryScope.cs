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

    // Filler that is market/intent scope, never product identity ("best AC deals near me" → "AC",
    // "أفضل سعر مكيف في عمان" → "مكيف"). Arabic entries are matched after NormalizeForMatch (alef folding
    // + definite-article stripping), so one spelling covers its hamza/alef variants.
    private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        // English
        "in", "the", "a", "an", "for", "of", "and", "or", "best", "price", "prices",
        "buy", "cheap", "deal", "deals", "near", "me", "shop", "store", "stores", "online",
        // Arabic — prepositions/intent/commerce filler ("في"=in, "على"=on, "الى"=to, "افضل"=best,
        // "سعر"/"اسعار"=price, "شراء"=buy, "رخيص"=cheap, "عرض"/"عروض"=deals, "متجر"/"محل"=store,
        // "اونلاين"=online). "من" (from) is deliberately OMITTED — it also occurs inside product names.
        "في", "على", "الى", "افضل", "سعر", "اسعار", "شراء", "رخيص",
        "عرض", "عروض", "متجر", "محل", "اونلاين", "توصيل",
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

        // Everything we drop, in NORMALIZED form (alef-folded, definite-article-stripped) so an Arabic
        // query's geo/filler is matched regardless of spelling variant or a leading "ال".
        var drop = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in Stopwords)
        {
            drop.Add(NormalizeForMatch(w));
        }
        if (!string.IsNullOrWhiteSpace(geo))
        {
            var profile = GeoProfiles.ResolveOrDefault(geo);
            foreach (var g in Tokenize(geo!)
                .Concat(Tokenize(profile.Country))
                .Concat(Tokenize(profile.CenterCity))
                .Concat(profile.NativeAliases.SelectMany(Tokenize)))
            {
                drop.Add(NormalizeForMatch(g));
            }
        }

        // Keep the ORIGINAL token in the output (the real product word); only the comparison is normalized.
        return Tokenize(query!)
            .Where(t => t.Length >= 2 && !drop.Contains(NormalizeForMatch(t)))
            .ToList();
    }

    /// <summary>
    /// Fold an Arabic (or English) token to a comparison key: normalize alef variants (أ إ آ ٱ → ا),
    /// drop tatweel, and strip a leading definite article "ال" when ≥2 chars remain, then lower-case.
    /// Used only for MATCHING against the stop/geo sets — the emitted tokens keep their original form.
    /// </summary>
    internal static string NormalizeForMatch(string token)
    {
        var sb = new System.Text.StringBuilder(token.Length);
        foreach (var c in token)
        {
            // Drop Arabic diacritics (tashkeel U+064B–U+0652, superscript alef U+0670) and tatweel (U+0640).
            if ((c >= 'ً' && c <= 'ْ') || c == 'ٰ' || c == 'ـ')
            {
                continue;
            }
            // Fold alef variants to bare alef.
            sb.Append(c is 'أ' or 'إ' or 'آ' or 'ٱ' ? 'ا' : c);
        }
        var s = sb.ToString();
        if (s.Length >= 4 && s.StartsWith("ال", StringComparison.Ordinal))
        {
            s = s[2..];
        }
        return s.ToLowerInvariant();
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
