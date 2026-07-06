namespace Daleel.Web.Data;

/// <summary>
/// One product model belonging to a <see cref="Brand"/>, harvested from the brand's own (local/regional)
/// website. Where <see cref="Brand"/> is the catalogue-level profile, this is the per-model row that
/// builds out a searchable model database: specs, image, and the local-vs-global price split a shopper
/// in-market actually cares about.
/// </summary>
/// <remarks>
/// Upsert-keyed by (<see cref="BrandId"/>, <see cref="ModelKey"/>) so re-harvesting a brand updates its
/// models in place rather than duplicating them. <see cref="ImageUrl"/> holds the R2-hosted copy when
/// image storage is configured, falling back to the original source URL otherwise.
/// <see cref="LastRefreshed"/> persists as Unix-ms bigint for provider-agnostic staleness filtering.
/// </remarks>
public sealed class BrandModel
{
    public int Id { get; set; }

    /// <summary>Owning brand (FK). Models are deleted with their brand.</summary>
    public int BrandId { get; set; }

    /// <summary>Navigation to the owning <see cref="Brand"/>.</summary>
    public Brand? Brand { get; set; }

    /// <summary>Display model name, e.g. "Galaxy S24 Ultra".</summary>
    public string ModelName { get; set; } = string.Empty;

    /// <summary>Normalized model name (trimmed, lower-cased) — the per-brand unique upsert key.</summary>
    public string ModelKey { get; set; } = string.Empty;

    /// <summary>Product category as classified by the catalogue, e.g. "Smartphone", "Air Conditioner".</summary>
    public string? Category { get; set; }

    /// <summary>Free-form specs serialized as a JSON object (key → value), or null when none were found.</summary>
    public string? SpecsJson { get; set; }

    /// <summary>Image URL — the R2-hosted copy when storage is configured, else the original source.</summary>
    public string? ImageUrl { get; set; }

    /// <summary>Local/in-market price (the brand's regional site), when listed.</summary>
    public decimal? LocalPrice { get; set; }

    /// <summary>Global/reference price (the brand's global site), when listed.</summary>
    public decimal? GlobalPrice { get; set; }

    /// <summary>Currency for the prices above, when known.</summary>
    public string? Currency { get; set; }

    /// <summary>Whether the model is shown as in-stock/available on the brand site.</summary>
    public bool IsAvailable { get; set; }

    /// <summary>The page the model was harvested from.</summary>
    public string? SourceUrl { get; set; }

    /// <summary>
    /// Which site-hierarchy level's catalogue this row came from — a <see cref="BrandSiteLevel"/>
    /// constant. Null = legacy/unattributed rows, treated as GLOBAL when filling specs/images.
    /// </summary>
    public string? SiteLevel { get; set; }

    /// <summary>
    /// Market scope of the harvest that produced this row: the ISO alpha-2 country code for
    /// local-site rows ("jo"), a region hint for regional ones, null for global/legacy.
    /// </summary>
    public string? SiteCountry { get; set; }

    public DateTimeOffset LastRefreshed { get; set; }

    // ── Smart product identification (vision pipeline) ───────────────────────────

    /// <summary>
    /// The canonical, merged-and-cleaned spec sheet for this model, serialized as a JSON object
    /// (key → value). This is what the UI reads — never the raw scraped data. Produced by the spec
    /// merger from every source (brand site, store listings, reviews) and normalized against the
    /// category schema. Null until the merge step has run.
    /// </summary>
    public string? FinalSpecsJson { get; set; }

    /// <summary>R2 URL of the canonical spec sheet (<c>final-specs/{brand}/{model}.json</c>), when stored.</summary>
    public string? FinalSpecsR2Url { get; set; }

    /// <summary>
    /// Every R2-hosted image discovered for this model (current + discontinued shots), so vision
    /// matching compares a store photo against the full catalogue, not just the primary
    /// <see cref="ImageUrl"/>. Stored as a JSON string array; unioned across regional crawls.
    /// </summary>
    public List<string> ImageR2Urls { get; set; } = new();

    /// <summary>
    /// Regional model numbers/aliases this canonical model is also sold under (e.g. a Jordan SKU vs
    /// the global model number). Unioned across regional crawls so a store's region-specific name
    /// still resolves to this one row. Stored as a JSON string array.
    /// </summary>
    public List<string> RegionalAliases { get; set; } = new();

    /// <summary>When this model was first discovered by the catalogue searcher (set once, never overwritten).</summary>
    public DateTimeOffset DiscoveredAt { get; set; }

    /// <summary>
    /// True when the model is no longer listed in the brand's live catalogue but is retained for
    /// historical matching (a store may still sell old stock under a discontinued model name).
    /// </summary>
    public bool IsDiscontinued { get; set; }

    /// <summary>Normalizes a model name into its per-brand lookup key (case/whitespace-insensitive).</summary>
    public static string Normalize(string name) => (name ?? string.Empty).Trim().ToLowerInvariant();

    public bool IsStale(DateTimeOffset now, TimeSpan ttl) => now - LastRefreshed > ttl;
}
