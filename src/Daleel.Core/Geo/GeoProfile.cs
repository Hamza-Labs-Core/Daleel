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
}
