namespace Daleel.Core.Geo;

/// <summary>
/// Describes a target market: its language preferences, default search-engine geo
/// parameters, which social platforms matter locally, and a representative city
/// center for proximity-based store search.
/// </summary>
/// <remarks>
/// A <see cref="GeoProfile"/> is the single place that encodes "how to research this
/// country". The agent reads it to decide which languages to generate queries in,
/// which Apify actors and social platforms to prioritize, and what <c>gl</c>/<c>hl</c>
/// parameters to pass to search APIs. Pre-built profiles live in <see cref="GeoProfiles"/>.
/// </remarks>
public record GeoProfile
{
    /// <summary>Canonical lowercase key, e.g. "jordan".</summary>
    public required string Key { get; init; }

    /// <summary>Human-readable country name.</summary>
    public required string Country { get; init; }

    /// <summary>ISO 3166-1 alpha-2 country code (used as the search <c>gl</c> param).</summary>
    public required string CountryCode { get; init; }

    /// <summary>
    /// BCP-47 language codes in priority order. The first is the market's primary
    /// language; the agent generates queries in all listed languages.
    /// </summary>
    public required IReadOnlyList<string> Languages { get; init; }

    /// <summary>Local currency code (ISO 4217), e.g. "JOD".</summary>
    public required string Currency { get; init; }

    /// <summary>Social platforms that matter in this market, most important first.</summary>
    public required IReadOnlyList<string> SocialPlatforms { get; init; }

    /// <summary>Local classifieds / marketplace domains worth scraping.</summary>
    public IReadOnlyList<string> Marketplaces { get; init; } = Array.Empty<string>();

    /// <summary>Apify actor ids preferred for this market's social platforms.</summary>
    public IReadOnlyList<string> ApifyActors { get; init; } = Array.Empty<string>();

    /// <summary>Representative city center for radius-based store search.</summary>
    public required GeoPoint Center { get; init; }

    /// <summary>Name of that city center, e.g. "Amman".</summary>
    public required string CenterCity { get; init; }

    /// <summary>The primary language code (first entry of <see cref="Languages"/>).</summary>
    public string PrimaryLanguage => Languages.Count > 0 ? Languages[0] : "en";

    /// <summary>True when Arabic is this market's primary language.</summary>
    public bool IsArabicFirst => PrimaryLanguage.StartsWith("ar", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Rough lat/lng bounding box(es) of the market's own territory, used to decide whether a
    /// user-shared location actually PLACES the user in this market (see
    /// <see cref="GeoProfiles.MarketContaining"/>). Multiple boxes let an irregular border be
    /// approximated (e.g. Jordan's south-western Aqaba corner).
    /// </summary>
    public IReadOnlyList<GeoBounds> Bounds { get; init; } = Array.Empty<GeoBounds>();
}

/// <summary>An inclusive lat/lng rectangle used for coarse "is this point in the country" checks.</summary>
public readonly record struct GeoBounds(double MinLat, double MaxLat, double MinLng, double MaxLng)
{
    public bool Contains(double lat, double lng) =>
        lat >= MinLat && lat <= MaxLat && lng >= MinLng && lng <= MaxLng;
}

/// <summary>Registry of pre-built market profiles, looked up by key or country code.</summary>
public static class GeoProfiles
{
    public static readonly GeoProfile Jordan = new()
    {
        Key = "jordan",
        Country = "Jordan",
        CountryCode = "jo",
        Languages = new[] { "ar", "en" },
        Currency = "JOD",
        SocialPlatforms = new[] { "facebook", "instagram", "whatsapp" },
        Marketplaces = new[] { "opensooq.com", "jordan.dubizzle.com", "carrefourjordan.com" },
        ApifyActors = new[] { "apify/facebook-groups-scraper", "scrapeforge/facebook-search-posts" },
        Center = new GeoPoint(31.9539, 35.9106),
        CenterCity = "Amman",
        // Two boxes: the main body east of the Jordan River (min-lng 35.60 keeps Jerusalem / the
        // West Bank out) plus the south-western Aqaba corner.
        Bounds = new[] { new GeoBounds(30.5, 33.40, 35.60, 39.30), new GeoBounds(29.18, 30.5, 34.88, 38.00) }
    };

    public static readonly GeoProfile SaudiArabia = new()
    {
        Key = "saudi",
        Country = "Saudi Arabia",
        CountryCode = "sa",
        Languages = new[] { "ar", "en" },
        Currency = "SAR",
        SocialPlatforms = new[] { "twitter", "instagram", "snapchat", "facebook" },
        Marketplaces = new[] { "haraj.com.sa", "opensooq.com", "noon.com" },
        ApifyActors = new[] { "apidojo/twitter-scraper", "apify/instagram-scraper" },
        Center = new GeoPoint(24.7136, 46.6753),
        CenterCity = "Riyadh",
        // Two boxes: the main body (south of lat 29.6, including the Red Sea coast and NEOM strip)
        // and the northern region east of lng 36.5 (Al Qurayyat/Turaif/Arar) — a single rectangle
        // would sweep over Israel/Palestine/Lebanon at its north-western corner. Qatar/Kuwait/Bahrain
        // still fall inside and are carved out by the NonMarkets exclusion list in MarketContaining.
        Bounds = new[] { new GeoBounds(16.29, 29.60, 34.48, 55.67), new GeoBounds(29.60, 32.16, 36.50, 49.00) }
    };

    public static readonly GeoProfile Uae = new()
    {
        Key = "uae",
        Country = "United Arab Emirates",
        CountryCode = "ae",
        Languages = new[] { "ar", "en" },
        Currency = "AED",
        SocialPlatforms = new[] { "instagram", "twitter", "facebook" },
        Marketplaces = new[] { "dubizzle.com", "noon.com", "amazon.ae" },
        ApifyActors = new[] { "apify/instagram-scraper", "apidojo/twitter-scraper" },
        Center = new GeoPoint(25.2048, 55.2708),
        CenterCity = "Dubai",
        // Min-lng 51.58 (the UAE's western tip) keeps the Qatar peninsula out.
        Bounds = new[] { new GeoBounds(22.63, 26.10, 51.58, 56.40) }
    };

    public static readonly GeoProfile Egypt = new()
    {
        Key = "egypt",
        Country = "Egypt",
        CountryCode = "eg",
        Languages = new[] { "ar", "en" },
        Currency = "EGP",
        SocialPlatforms = new[] { "facebook", "instagram", "tiktok" },
        Marketplaces = new[] { "olx.com.eg", "jumia.com.eg", "souq.com" },
        ApifyActors = new[] { "apify/facebook-groups-scraper", "scrapeforge/facebook-search-posts" },
        Center = new GeoPoint(30.0444, 31.2357),
        CenterCity = "Cairo",
        Bounds = new[] { new GeoBounds(21.99, 31.68, 24.70, 36.90) }
    };

    public static readonly GeoProfile Usa = new()
    {
        Key = "usa",
        Country = "United States",
        CountryCode = "us",
        Languages = new[] { "en" },
        Currency = "USD",
        SocialPlatforms = new[] { "instagram", "twitter", "reddit", "facebook" },
        Marketplaces = new[] { "amazon.com", "ebay.com", "craigslist.org" },
        ApifyActors = new[] { "apify/instagram-scraper", "apidojo/twitter-scraper" },
        Center = new GeoPoint(40.7128, -74.0060),
        CenterCity = "New York",
        // Contiguous US. Alaska/Hawaii users fall through to the inline market prompt — acceptable
        // for a coarse consent-gated locator; they can also just name the market in the query.
        Bounds = new[] { new GeoBounds(24.40, 49.40, -125.00, -66.90) }
    };

    private static readonly IReadOnlyDictionary<string, GeoProfile> ByKey =
        new[] { Jordan, SaudiArabia, Uae, Egypt, Usa }
            .ToDictionary(p => p.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>All registered profiles.</summary>
    public static IReadOnlyCollection<GeoProfile> All => (IReadOnlyCollection<GeoProfile>)ByKey.Values;

    /// <summary>
    /// Resolves a profile by key ("jordan"), country name ("Jordan"), or country code
    /// ("jo"), case-insensitively. Returns null when nothing matches.
    /// </summary>
    public static GeoProfile? Resolve(string? keyOrCodeOrName)
    {
        if (string.IsNullOrWhiteSpace(keyOrCodeOrName))
        {
            return null;
        }

        var needle = keyOrCodeOrName.Trim();
        if (ByKey.TryGetValue(needle, out var byKey))
        {
            return byKey;
        }

        return ByKey.Values.FirstOrDefault(p =>
            p.CountryCode.Equals(needle, StringComparison.OrdinalIgnoreCase) ||
            p.Country.Equals(needle, StringComparison.OrdinalIgnoreCase) ||
            p.CenterCity.Equals(needle, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Resolves a profile, falling back to <see cref="Usa"/> when unknown.</summary>
    public static GeoProfile ResolveOrDefault(string? keyOrCodeOrName) =>
        Resolve(keyOrCodeOrName) ?? Usa;

    /// <summary>
    /// Neighbouring non-market territories that fall inside a market's coarse bounding box (Qatar,
    /// Kuwait, Bahrain sit inside Saudi Arabia's). A location fix landing here must NOT silently
    /// resolve to the surrounding market — the caller falls through to asking the user.
    /// </summary>
    private static readonly GeoBounds[] NonMarkets =
    {
        new(24.40, 26.20, 50.70, 51.70), // Qatar
        new(28.50, 30.10, 46.50, 48.50), // Kuwait
        new(25.50, 26.40, 50.30, 50.80), // Bahrain
    };

    // Containment is checked smallest-market-first so a point inside overlapping boxes (Aqaba sits in
    // both Jordan's and Saudi Arabia's) resolves to the country it is actually in.
    private static readonly GeoProfile[] ByBoundsSpecificity = { Jordan, Uae, Egypt, SaudiArabia, Usa };

    /// <summary>
    /// Maps a user-shared coordinate (the browser's consent-gated geolocation) to the supported market
    /// whose territory actually CONTAINS it, or null when the user isn't in any supported market. This
    /// is the second market-detection step, after <see cref="DetectInText"/> — a null makes the UI ask
    /// the user to pick. Deliberately strict: a visitor in Berlin or Doha who shares their location is
    /// ASKED, never silently assigned to whichever market happens to be nearest.
    /// </summary>
    public static GeoProfile? MarketContaining(double latitude, double longitude)
    {
        foreach (var exclusion in NonMarkets)
        {
            if (exclusion.Contains(latitude, longitude))
            {
                return null;
            }
        }

        foreach (var profile in ByBoundsSpecificity)
        {
            foreach (var box in profile.Bounds)
            {
                if (box.Contains(latitude, longitude))
                {
                    return profile;
                }
            }
        }

        return null;
    }

    // Distinctive country/city indicators (English + Arabic) for detecting the market straight from a
    // query like "best AC in Dubai" or "غسالة في عمان". 2-letter ISO codes (jo/sa/ae/eg/us) are
    // deliberately NOT here — too short, they'd false-match ordinary words ("us", "use", "sale"). The
    // 3-letter ISO-4217 currency codes (jod/sar/aed/egp/usd) ARE included: they're unambiguous market
    // signals ("earbuds under 100 JOD" → Jordan) that don't collide with common words.
    private static readonly (GeoProfile Profile, string[] Terms)[] QueryIndicators =
    {
        (Jordan, new[] { "jordan", "jordanian", "amman", "irbid", "zarqa", "aqaba", "jod",
            "الأردن", "الاردن", "عمان", "عمّان", "اربد", "إربد", "الزرقاء", "العقبة", "أردني", "دينار أردني" }),
        (SaudiArabia, new[] { "saudi", "saudi arabia", "ksa", "riyadh", "jeddah", "jiddah", "mecca",
            "makkah", "medina", "dammam", "khobar", "sar",
            "السعودية", "السعوديه", "الرياض", "جدة", "مكة", "المدينة", "الدمام", "الخبر", "المملكة", "ريال سعودي" }),
        (Uae, new[] { "uae", "u.a.e", "united arab emirates", "emirates", "emirati", "dubai", "abu dhabi",
            "abudhabi", "sharjah", "ajman", "aed",
            "الإمارات", "الامارات", "دبي", "أبوظبي", "ابوظبي", "الشارقة", "عجمان", "إماراتي", "درهم إماراتي" }),
        (Egypt, new[] { "egypt", "egyptian", "cairo", "alexandria", "giza", "egp",
            "مصر", "مصري", "القاهرة", "الإسكندرية", "الاسكندرية", "الجيزة", "جنيه مصري" }),
        (Usa, new[] { "usa", "u.s.a", "united states", "america", "american", "new york",
            "los angeles", "chicago", "usd", "أمريكا", "امريكا", "الولايات المتحدة" }),
    };

    /// <summary>
    /// Detects the intended market straight from free-text (typically the search query) — the
    /// strongest signal of where the user is shopping ("best AC in Dubai" → UAE), overriding any
    /// stored/auto-detected default. Returns the market mentioned earliest in the text, or null when
    /// none of the supported markets is named.
    /// </summary>
    public static GeoProfile? DetectInText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var t = text.ToLowerInvariant();
        GeoProfile? best = null;
        var bestIndex = int.MaxValue;

        foreach (var (profile, terms) in QueryIndicators)
        {
            foreach (var term in terms)
            {
                var idx = IndexOfTerm(t, term);
                if (idx >= 0 && idx < bestIndex)
                {
                    best = profile;
                    bestIndex = idx;
                }
            }
        }

        return best;
    }

    /// <summary>Word-boundary match for ASCII terms (so "usa" ≠ "usable"); plain contains for Arabic.</summary>
    private static int IndexOfTerm(string text, string term)
    {
        var ascii = term.All(c => c < 128);
        if (!ascii)
        {
            return text.IndexOf(term, StringComparison.Ordinal);
        }

        var m = System.Text.RegularExpressions.Regex.Match(
            text, $@"\b{System.Text.RegularExpressions.Regex.Escape(term)}\b");
        return m.Success ? m.Index : -1;
    }
}
