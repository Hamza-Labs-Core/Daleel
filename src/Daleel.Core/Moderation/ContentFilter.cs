using System.Text.RegularExpressions;
using Daleel.Core.Arabic;
using Daleel.Core.Models;

namespace Daleel.Core.Moderation;

/// <summary>How aggressively the <see cref="ContentFilter"/> screens content.</summary>
public enum FilterStrictness
{
    /// <summary>No filtering — admin-only escape hatch.</summary>
    Off,

    /// <summary>Blocks the core haram categories (alcohol, pork, gambling, adult, drugs).</summary>
    Moderate,

    /// <summary>Moderate plus tobacco and interest-based finance (riba). The default.</summary>
    Strict
}

/// <summary>
/// A bilingual (Arabic + English) halal content moderator. Screens free text and result
/// collections, removing anything that mentions a blocked category. Matches are recorded in
/// <see cref="AuditLog"/> so the application can log what was filtered without ever showing it.
/// </summary>
/// <remarks>
/// English terms match on word boundaries (with an optional trailing "s") so "bar" catches
/// "City Bar" / "bars" but not "barber". Arabic terms are matched against the canonical
/// <see cref="ArabicNormalizer"/> form, so orthographic variants (hamza/alef/taa-marbuta) all hit.
/// </remarks>
public sealed class ContentFilter
{
    /// <summary>A blocked category and its bilingual trigger terms.</summary>
    public sealed record Category(string Name, FilterStrictness MinLevel, string[] English, string[] Arabic)
    {
        /// <summary>
        /// The English terms compiled once into a single word-boundaried alternation
        /// (<c>\b(?:beer|wine|…)s?\b</c>). Built lazily on first use and cached for the process —
        /// this avoids recompiling a regex per term per item on the hot filtering path.
        /// </summary>
        public Regex EnglishPattern { get; } = BuildEnglishPattern(English);

        /// <summary>The Arabic trigger terms, pre-normalized once via <see cref="ArabicNormalizer"/>.</summary>
        public string[] NormalizedArabic { get; } = Array.ConvertAll(Arabic, ArabicNormalizer.Normalize);

        private static Regex BuildEnglishPattern(string[] terms)
        {
            var alternation = string.Join('|', terms.Select(Regex.Escape));
            return new Regex($@"\b(?:{alternation})s?\b",
                RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
    }

    private static readonly Category[] Categories =
    {
        new("alcohol", FilterStrictness.Moderate,
            new[] { "alcohol", "alcoholic", "beer", "wine", "whisky", "whiskey", "vodka", "liquor", "liqueur",
                    "brandy", "rum", "tequila", "champagne", "cocktail", "brewery", "winery", "pub", "bar",
                    "spirits", "booze", "cider" },
            new[] { "خمر", "خمور", "خمارة", "كحول", "بيرة", "نبيذ", "ويسكي", "فودكا", "مشروبات كحولية",
                    "حانة", "بار", "مسكر", "شراب كحولي" }),

        new("pork", FilterStrictness.Moderate,
            new[] { "pork", "bacon", "ham", "lard", "swine", "prosciutto", "pancetta", "non-halal" },
            new[] { "خنزير", "لحم خنزير", "لحم الخنزير", "خنزيري", "غير حلال" }),

        new("gambling", FilterStrictness.Moderate,
            new[] { "gambling", "casino", "betting", "lottery", "poker", "roulette", "slots", "wager", "bookmaker" },
            new[] { "قمار", "كازينو", "رهان", "مراهنة", "يانصيب", "بوكر", "ميسر" }),

        new("adult", FilterStrictness.Moderate,
            new[] { "porn", "pornography", "xxx", "nude", "nudity", "escort", "adult content", "erotic", "sex shop" },
            new[] { "إباحي", "إباحية", "عاري", "دعارة", "جنسي" }),

        new("drugs", FilterStrictness.Moderate,
            new[] { "cannabis", "marijuana", "cocaine", "heroin", "narcotic", "narcotics", "weed", "hashish" },
            new[] { "مخدرات", "حشيش", "كوكايين", "هيروين", "ماريجوانا", "ماريغوانا" }),

        new("tobacco", FilterStrictness.Strict,
            new[] { "cigarette", "cigar", "tobacco", "vape", "vaping", "e-cigarette", "shisha", "hookah", "nicotine" },
            new[] { "سجائر", "سيجارة", "تبغ", "دخان", "شيشة", "أركيلة", "نرجيلة", "نيكوتين" }),

        new("riba", FilterStrictness.Strict,
            new[] { "interest rate", "interest-based", "usury", "payday loan", "conventional mortgage" },
            new[] { "ربا", "فائدة ربوية", "قرض ربوي", "فوائد ربوية" }),
    };

    private readonly FilterStrictness _strictness;
    private readonly List<string> _audit = new();

    public ContentFilter(FilterStrictness strictness = FilterStrictness.Strict) => _strictness = strictness;

    /// <summary>The configured strictness level.</summary>
    public FilterStrictness Strictness => _strictness;

    /// <summary>Terms/categories that triggered removals, in order — for auditing, never display.</summary>
    public IReadOnlyList<string> AuditLog => _audit;

    /// <summary>True when the text is free of any blocked term at the current strictness.</summary>
    public bool IsHalal(string? text)
    {
        if (_strictness == FilterStrictness.Off || string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        return MatchCategory(text) is null;
    }

    /// <summary>Returns the text unchanged when halal, or null when the whole text is flagged.</summary>
    public string? FilterText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || _strictness == FilterStrictness.Off)
        {
            return text;
        }

        if (MatchCategory(text) is { } category)
        {
            _audit.Add($"text:{category}");
            return null;
        }

        return text;
    }

    /// <summary>Generic filter: keeps only items whose projected text is halal.</summary>
    public List<T> FilterResults<T>(IEnumerable<T> items, Func<T, string?> textSelector)
    {
        if (_strictness == FilterStrictness.Off)
        {
            return items.ToList();
        }

        var kept = new List<T>();
        foreach (var item in items)
        {
            var match = MatchCategory(textSelector(item));
            if (match is null)
            {
                kept.Add(item);
            }
            else
            {
                _audit.Add($"{typeof(T).Name}:{match}");
            }
        }

        return kept;
    }

    /// <summary>Removes deals for alcohol, pork, and other non-halal products.</summary>
    public List<DealListing> FilterDeals(IEnumerable<DealListing> deals) =>
        FilterResults(deals, d => $"{d.Title} {d.Product} {d.Store}");

    /// <summary>Removes brand-report deal rows that mention blocked content.</summary>
    public List<DealResult> FilterDealResults(IEnumerable<DealResult> deals) =>
        FilterResults(deals, d => $"{d.Title} {d.Product} {d.Store}");

    /// <summary>Removes bars, liquor stores, and other non-halal venues.</summary>
    public List<StoreLocation> FilterStores(IEnumerable<StoreLocation> stores) =>
        FilterResults(stores, s => $"{s.Name} {s.Address}");

    /// <summary>Removes store results (lighter web/shopping form) with blocked content.</summary>
    public List<StoreResult> FilterStoreResults(IEnumerable<StoreResult> stores) =>
        FilterResults(stores, s => $"{s.Name} {s.Source}");

    /// <summary>Removes social posts / opinions containing blocked content.</summary>
    public List<SocialPost> FilterSocialPosts(IEnumerable<SocialPost> posts) =>
        FilterResults(posts, p => $"{p.Text} {p.Author}");

    /// <summary>The name of the first category the text trips, or null if clean.</summary>
    public string? MatchCategory(string? text)
    {
        if (_strictness == FilterStrictness.Off || string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var latin = text.ToLowerInvariant();
        var arabic = ArabicNormalizer.Normalize(text);

        foreach (var category in Categories)
        {
            if (category.MinLevel > _strictness)
            {
                continue; // category not active at this strictness
            }

            if (category.EnglishPattern.IsMatch(latin))
            {
                return category.Name;
            }

            foreach (var term in category.NormalizedArabic)
            {
                if (arabic.Contains(term, StringComparison.Ordinal))
                {
                    return category.Name;
                }
            }
        }

        return null;
    }
}
