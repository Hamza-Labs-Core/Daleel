namespace Daleel.Web.Data;

/// <summary>
/// A persisted, periodically-refreshed store/retailer profile — the store-side counterpart to
/// <see cref="Brand"/>. Built once by the <c>StoreProfileService</c> via Context.dev + the LLM and
/// joined against search results to enrich the retailers a search surfaces.
/// </summary>
/// <remarks>
/// Same persistence shape as <see cref="Brand"/>: upsert-keyed by <see cref="NameKey"/>,
/// <see cref="BrandsCarried"/> stored as JSON, and <see cref="LastRefreshed"/> as Unix-ms so the
/// staleness sweep can filter on it in SQLite.
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

    public DateTimeOffset LastRefreshed { get; set; }

    public static string Normalize(string name) => (name ?? string.Empty).Trim().ToLowerInvariant();

    public bool IsStale(DateTimeOffset now, TimeSpan ttl) => now - LastRefreshed > ttl;
}
