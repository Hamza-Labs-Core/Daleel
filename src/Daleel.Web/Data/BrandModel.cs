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
/// <see cref="LastRefreshed"/> persists as Unix-ms for SQLite-translatable staleness filtering.
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

    public DateTimeOffset LastRefreshed { get; set; }

    /// <summary>Normalizes a model name into its per-brand lookup key (case/whitespace-insensitive).</summary>
    public static string Normalize(string name) => (name ?? string.Empty).Trim().ToLowerInvariant();

    public bool IsStale(DateTimeOffset now, TimeSpan ttl) => now - LastRefreshed > ttl;
}
