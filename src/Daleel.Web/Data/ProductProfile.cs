namespace Daleel.Web.Data;

/// <summary>
/// A persisted, periodically-refreshed product/model deep-dive — the item-side counterpart to
/// <see cref="Brand"/> and <see cref="Store"/>. Built once by the per-item deep-dive (scraping the
/// model's offer page via Context.dev) and reused by every later search that surfaces the same model,
/// so a deep-dive is paid for once rather than on every search.
/// </summary>
/// <remarks>
/// Same persistence shape as <see cref="Store"/>: upsert-keyed by <see cref="NameKey"/> (normalized
/// brand+model), and <see cref="LastRefreshed"/> stored as Unix-ms so the staleness sweep can filter
/// on it provider-agnostically.
/// </remarks>
public sealed class ProductProfile
{
    public int Id { get; set; }

    /// <summary>Display name as surfaced, e.g. "Samsung AR24 Wind-Free".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Normalized (brand+model, trimmed, lower-cased) — the unique upsert/lookup key.</summary>
    public string NameKey { get; set; } = string.Empty;

    public string? Brand { get; set; }
    public string? Model { get; set; }

    /// <summary>The scraped detail (markdown specs/description) distilled from the offer page.</summary>
    public string? Details { get; set; }

    /// <summary>
    /// The canonical, merged-and-cleaned spec sheet for this item, serialized as a JSON object
    /// (key → value). Written by the per-item deep-dive for <em>every</em> enriched item (not only
    /// freshly-scraped ones), so the dedicated product page can render a clean spec table even when no
    /// harvested <see cref="BrandModel"/> backs the listing. Null when the item produced no structured specs.
    /// </summary>
    public string? SpecsJson { get; set; }

    /// <summary>The page the details were scraped from.</summary>
    public string? SourceUrl { get; set; }

    public DateTimeOffset LastRefreshed { get; set; }

    /// <summary>
    /// Builds the normalized upsert key from a model's brand + model (falling back to its name),
    /// so the same product researched from two different searches maps to one row. Returns "" when
    /// there's nothing usable to key on.
    /// </summary>
    public static string KeyFor(string? brand, string? model, string name)
    {
        var basis = !string.IsNullOrWhiteSpace(brand) || !string.IsNullOrWhiteSpace(model)
            ? $"{brand} {model}"
            : name;
        return Normalize(basis);
    }

    // Delegates to the shared product-identity normalization so the persisted ProductKey and the
    // StableId.ForProduct routing id are always derived from the exact same basis (see L-7 / StableId).
    public static string Normalize(string value) => Daleel.Core.Models.StableId.NormalizeIdentity(value);

    public bool IsStale(DateTimeOffset now, TimeSpan ttl) => now - LastRefreshed > ttl;
}
