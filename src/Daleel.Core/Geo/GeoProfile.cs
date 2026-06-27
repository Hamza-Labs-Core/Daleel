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
        CenterCity = "Amman"
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
        CenterCity = "Riyadh"
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
        CenterCity = "Dubai"
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
        CenterCity = "Cairo"
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
        CenterCity = "New York"
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
    /// Maps a raw coordinate (typically from the browser's geolocation API) to the supported market
    /// whose <see cref="GeoProfile.Center"/> is closest by great-circle distance. This is the second
    /// market-detection step, after <see cref="DetectInText"/> and before asking the user. There is
    /// deliberately no max-distance cutoff: a successful location fix always resolves to a market — a
    /// visitor far from every supported market is expected to name it in the query text instead.
    /// Returns null only when no profiles are registered.
    /// </summary>
    public static GeoProfile? NearestTo(double latitude, double longitude)
    {
        GeoProfile? nearest = null;
        var bestKm = double.MaxValue;
        foreach (var profile in ByKey.Values)
        {
            var km = HaversineKm(latitude, longitude, profile.Center.Latitude, profile.Center.Longitude);
            if (km < bestKm)
            {
                bestKm = km;
                nearest = profile;
            }
        }

        return nearest;
    }

    /// <summary>Great-circle distance in kilometres between two lat/lng coordinates (Haversine).</summary>
    private static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double earthRadiusKm = 6371.0;
        static double ToRad(double deg) => deg * Math.PI / 180.0;

        var dLat = ToRad(lat2 - lat1);
        var dLon = ToRad(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(ToRad(lat1)) * Math.Cos(ToRad(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * earthRadiusKm * Math.Asin(Math.Min(1.0, Math.Sqrt(a)));
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
