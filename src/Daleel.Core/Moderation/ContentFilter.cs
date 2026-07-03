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

    /// <summary>
    /// Moderate plus tobacco. The default.
    /// </summary>
    /// <remarks>
    /// Note: a store's <em>financing model</em> (riba / interest-based banking) is deliberately NOT
    /// screened at any level — see the policy note on <see cref="Categories"/>.
    /// </remarks>
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

    // Halal-compliance policy — what we filter and what we deliberately do NOT.
    //
    // We filter haram *content*: things a result actually sells, shows, or promotes that are
    // themselves forbidden — alcohol/wine, pork, gambling, adult/immodest content, drugs, tobacco.
    // For a shopping assistant the user is choosing a *product or venue*, so blocking these protects
    // them from being shown the haram item itself.
    //
    // We do NOT filter a store by its *business model* or financing options. A bank, a financial
    // institution, or any retailer that happens to offer riba (interest-based) installment plans is
    // NOT haram for the purpose of this assistant: the user can still walk in and pay cash for a
    // perfectly halal TV or fridge. Filtering the store would wrongly hide a legitimate option over a
    // payment method the user need not use. Hence there is intentionally no "riba"/"banking" category
    // here — keep it that way. (Previously a Strict-level "riba" category existed; it was removed
    // because it conflated the store's financing with the halal status of what it sells.)
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

        // No "riba"/"banking" category: a store's financing model is not haram content — see the
        // policy note above. Banks and interest-offering retailers must remain visible in results.
    };

    private readonly FilterStrictness _strictness;
    private readonly List<string> _audit = new();
    private readonly List<FilterFinding> _details = new();
    // Whitelist keys (source URLs, image URLs, content hashes) that admins have explicitly
    // un-filtered. An item matching any key bypasses every classifier — the feedback loop's "undo".
    private readonly HashSet<string> _whitelist;
    // One ContentFilter is shared by reference across up to MaxConcurrency=5 parallel sub-workflows
    // (the halal moderation chokepoint). Record() and the audit readers run on those threads, so every
    // touch of _audit/_details is serialized here — unlocked List.Add corrupts the backing array.
    private readonly object _auditLock = new();

    public ContentFilter(FilterStrictness strictness = FilterStrictness.Strict,
        IReadOnlyCollection<string>? whitelist = null)
    {
        _strictness = strictness;
        _whitelist = whitelist is { Count: > 0 }
            ? new HashSet<string>(whitelist.Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k.Trim()),
                StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The configured strictness level.</summary>
    public FilterStrictness Strictness => _strictness;

    /// <summary>Terms/categories that triggered removals, in order — for auditing, never display.</summary>
    /// <remarks>Returns a snapshot: the backing list can be mutated concurrently by parallel filtering.</remarks>
    public IReadOnlyList<string> AuditLog
    {
        get { lock (_auditLock) { return _audit.ToArray(); } }
    }

    /// <summary>
    /// Structured findings (what was flagged, where, how it was decided) for the admin
    /// "Filtered content" log. Server/admin-only — never surfaced to end users. Includes
    /// image-strip findings whose item was kept; <see cref="AuditLog"/> counts removals only.
    /// </summary>
    /// <remarks>Returns a snapshot: the backing list can be mutated concurrently by parallel filtering.</remarks>
    public IReadOnlyList<FilterFinding> AuditDetails
    {
        get { lock (_auditLock) { return _details.ToArray(); } }
    }

    /// <summary>True when any of the item's stable keys was explicitly un-filtered by an admin.</summary>
    public bool IsWhitelisted(string? sourceUrl, string? imageUrl, string? contentHash)
    {
        if (_whitelist.Count == 0)
        {
            return false;
        }

        return (sourceUrl is not null && _whitelist.Contains(sourceUrl.Trim()))
            || (imageUrl is not null && _whitelist.Contains(imageUrl.Trim()))
            || (contentHash is not null && _whitelist.Contains(contentHash));
    }

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

        if (MatchDetail(text) is { } m)
        {
            Record(m.Category, m.Term, "text", text);
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
            var text = textSelector(item);
            if (MatchDetail(text) is { } m && !IsWhitelisted(null, null, ModerationKeys.HashContent(text)))
            {
                Record(m.Category, m.Term, typeof(T).Name, text);
            }
            else
            {
                kept.Add(item);
            }
        }

        return kept;
    }

    /// <summary>Records a keyword removal in both the legacy string log and the structured detail log.</summary>
    private void Record(string category, string term, string kind, string? content) =>
        RecordFinding(new FilterFinding(
            category, term, kind, Truncate(content), Field: null, SourceUrl: null, ImageUrl: null,
            Confidence: 1.0, FindingSource.Keyword, ModerationKeys.HashContent(content), ItemRemoved: true));

    /// <summary>
    /// Records a structured finding. Removals also bump the legacy per-removal string log
    /// (which is what <c>FilteredCount</c> is derived from); image-strip findings whose item
    /// survived appear only in <see cref="AuditDetails"/>.
    /// </summary>
    public void RecordFinding(FilterFinding finding)
    {
        lock (_auditLock)
        {
            if (finding.ItemRemoved)
            {
                _audit.Add($"{finding.Kind}:{finding.Category}");
            }

            _details.Add(finding with { Content = Truncate(finding.Content) });
        }
    }

    private static string Truncate(string? text)
    {
        var t = (text ?? string.Empty).Trim();
        return t.Length <= 200 ? t : t[..200] + "…";
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

    /// <summary>
    /// Field-aware match: checks each projected field separately and reports WHICH field tripped
    /// (title vs snippet vs seller…), so the admin log can show exactly where on the item the
    /// blocked term was found. Returns null when every field is clean.
    /// </summary>
    public (string Category, string Term, string Field)? MatchFields(
        IReadOnlyList<(string Field, string? Text)> fields)
    {
        foreach (var (field, text) in fields)
        {
            if (MatchDetail(text) is { } m)
            {
                return (m.Category, m.Term, field);
            }
        }

        return null;
    }

    /// <summary>The name of the first category the text trips, or null if clean.</summary>
    public string? MatchCategory(string? text) => MatchDetail(text)?.Category;

    /// <summary>
    /// The first category the text trips and the exact term that matched, or null if clean.
    /// The term lets the admin log show <em>which</em> blocklist word fired (e.g. "bar").
    /// </summary>
    public (string Category, string Term)? MatchDetail(string? text)
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

            var match = category.EnglishPattern.Match(latin);
            if (match.Success)
            {
                return (category.Name, match.Value);
            }

            foreach (var term in category.NormalizedArabic)
            {
                if (arabic.Contains(term, StringComparison.Ordinal))
                {
                    return (category.Name, term);
                }
            }
        }

        return null;
    }
}
