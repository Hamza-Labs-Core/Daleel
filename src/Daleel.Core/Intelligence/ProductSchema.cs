namespace Daleel.Core.Intelligence;

/// <summary>
/// A product-type-aware comparison schema: the set of spec fields that actually matter when
/// comparing products of a given kind. An AC is compared on BTU, energy rating and noise; a
/// phone on screen, RAM, storage and camera. The LLM determines the product type for a query
/// and fills this schema, which then drives the columns of the compare table and the spec rows
/// of the detail views — so the UI shows the <em>right</em> attributes per category instead of a
/// blind dump of free-form specs.
/// </summary>
public record ProductSchema
{
    /// <summary>
    /// The product type this schema describes, e.g. "air conditioner", "smartphone", "laptop".
    /// Free-form (the LLM decides) but normalized to lower case; "general" when unknown.
    /// </summary>
    public string ProductType { get; init; } = "general";

    /// <summary>The spec fields that matter for this product type, in display order.</summary>
    public IReadOnlyList<SpecField> Fields { get; init; } = Array.Empty<SpecField>();

    /// <summary>True when this schema carries no usable fields (the caller should fall back to free-form specs).</summary>
    public bool IsEmpty => Fields.Count == 0;

    /// <summary>A neutral, fields-less schema used when no product type could be determined.</summary>
    public static ProductSchema General { get; } = new();
}

/// <summary>How important a <see cref="SpecField"/> is — used to surface the headline specs first.</summary>
public enum SpecImportance
{
    /// <summary>A defining attribute for this product type (e.g. BTU for an AC) — shown prominently.</summary>
    Key,

    /// <summary>A useful-but-secondary attribute (e.g. warranty).</summary>
    Normal
}

/// <summary>
/// One comparable attribute within a <see cref="ProductSchema"/>. The <see cref="Key"/> is the
/// machine name the extractor fills (and looks up in a product's free-form specs); the
/// <see cref="Label"/> is what the UI shows.
/// </summary>
public record SpecField
{
    /// <summary>Stable machine key, lower-snake-case, e.g. "btu", "energy_rating", "ram_gb".</summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>Human label for the column/row, e.g. "BTU", "Energy rating", "RAM".</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>Optional unit suffix shown after the value, e.g. "BTU", "GB", "\"", "dB".</summary>
    public string? Unit { get; init; }

    /// <summary>
    /// Whether a higher value is better for this field (true for RAM/battery, false for noise/price).
    /// Lets the compare table highlight the best value per row. Null when not orderable (e.g. OS).
    /// </summary>
    public bool? HigherIsBetter { get; init; }

    /// <summary>Relative importance, so the UI can lead with the defining specs.</summary>
    public SpecImportance Importance { get; init; } = SpecImportance.Normal;
}
