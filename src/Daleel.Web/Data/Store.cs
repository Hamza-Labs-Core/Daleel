namespace Daleel.Web.Data;

/// <summary>
/// A persisted, periodically-refreshed store/retailer profile — the store-side counterpart to
/// <see cref="Brand"/>. Built once by the <c>StoreProfileService</c> via Context.dev + the LLM and
/// joined against search results to enrich the retailers a search surfaces.
/// </summary>
/// <remarks>
/// Same persistence shape as <see cref="Brand"/>: upsert-keyed by <see cref="NameKey"/>,
/// <see cref="BrandsCarried"/> stored as JSON, and <see cref="LastRefreshed"/> as Unix-ms so the
/// staleness sweep can filter on it provider-agnostically.
/// </remarks>
public sealed class Store
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Normalized (trimmed, lower-cased) name — the unique upsert/lookup key.</summary>
    public string NameKey { get; set; } = string.Empty;

    public string? Location { get; set; }

    /// <summary>Store kind, e.g. "electronics retailer", "marketplace", "official brand store".</summary>
    public string? Type { get; set; }

    public string? Website { get; set; }

    /// <summary>Brands the store is known to carry (used to cross-link brand ↔ store enrichment).</summary>
    public List<string> BrandsCarried { get; set; } = new();

    /// <summary>Aggregate rating on a 0–5 scale where known.</summary>
    public double? Rating { get; set; }

    // ── Contact + Google-Maps verification ───────────────────────────────────────
    // Populated when a store is verified against Google Places during a search (see
    // ContextDevProfileResearcher). Contact fields prefer scraped/Places data; the Google*
    // fields are the authoritative Places echo used to render an embedded map and "verified" badge.

    /// <summary>Phone number (Places international format, or scraped from the store site).</summary>
    public string? Phone { get; set; }

    /// <summary>Contact e-mail, when one was extractable from the scraped store page.</summary>
    public string? Email { get; set; }

    /// <summary>Formatted street address (Places <c>formattedAddress</c> when verified).</summary>
    public string? Address { get; set; }

    /// <summary>Latitude of the verified Google Places location, when known.</summary>
    public double? Latitude { get; set; }

    /// <summary>Longitude of the verified Google Places location, when known.</summary>
    public double? Longitude { get; set; }

    /// <summary>Human-readable opening-hours lines (Places <c>weekdayDescriptions</c>).</summary>
    public List<string> OpeningHours { get; set; } = new();

    /// <summary>Google Maps aggregate rating on a 0–5 scale (distinct from the LLM-assessed <see cref="Rating"/>).</summary>
    public double? GoogleRating { get; set; }

    /// <summary>Number of Google ratings behind <see cref="GoogleRating"/>.</summary>
    public int? GoogleReviewCount { get; set; }

    /// <summary>Google Places place id — the stable key for re-fetching details/reviews and the map link.</summary>
    public string? GooglePlaceId { get; set; }

    /// <summary>Canonical Google Maps URL for the verified place, when known.</summary>
    public string? GoogleMapsUrl { get; set; }

    public DateTimeOffset LastRefreshed { get; set; }

    /// <summary>True once the store has been cross-referenced against a Google Places entry.</summary>
    public bool IsVerified => !string.IsNullOrEmpty(GooglePlaceId);

    public static string Normalize(string name) => (name ?? string.Empty).Trim().ToLowerInvariant();

    public bool IsStale(DateTimeOffset now, TimeSpan ttl) => now - LastRefreshed > ttl;
}
