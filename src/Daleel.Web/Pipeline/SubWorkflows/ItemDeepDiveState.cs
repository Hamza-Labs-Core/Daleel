using Daleel.Core.Intelligence;
using Daleel.Core.Models;
using Daleel.Web.Identification;

namespace Daleel.Web.Pipeline.SubWorkflows;

/// <summary>
/// Per-item state for <see cref="ItemDeepDiveWorkflow"/>. Seeded with one extracted
/// <see cref="ProductModel"/>; the activities scrape its detail page for specs (DB-first, so a fresh
/// saved deep-dive is reused with no network), compare its store prices, collect reviews, and persist
/// the enriched profile — folding specs back onto <see cref="Result"/>.
/// </summary>
public sealed class ItemDeepDiveState : SubWorkflowState
{
    /// <summary>The product/model as extracted from the search (input).</summary>
    public ProductModel Model { get; set; } = default!;

    /// <summary>The enriched model (output). Starts equal to <see cref="Model"/>; folds in scraped specs.</summary>
    public ProductModel Result { get; set; } = default!;

    /// <summary>Normalized brand+model key for the saved <c>ProductProfile</c>.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>The scraped (or reused) spec markdown for this item, or null when none was obtained.</summary>
    public string? Details { get; set; }

    /// <summary>The page the specs came from (null when reused from the DB).</summary>
    public string? SourceUrl { get; set; }

    /// <summary>True when <see cref="Details"/> came from a fresh saved profile (no scrape, no re-save).</summary>
    public bool ReusedFromCache { get; set; }

    // ── Smart product identification + spec pipeline ─────────────────────────────

    /// <summary>The category comparison schema (seeded from the parent search), driving the spec merge order.</summary>
    public ProductSchema Schema { get; set; } = ProductSchema.General;

    /// <summary>The brand-model row this listing was identified as, or null when unidentified.</summary>
    public int? IdentifiedBrandModelId { get; set; }

    /// <summary>The product category resolved during identification, when known.</summary>
    public string? Category { get; set; }

    /// <summary>Confidence of the identification (1.0 for a text match, the vision score otherwise).</summary>
    public double MatchConfidence { get; set; }

    /// <summary>How the listing was identified: "text", "vision", or null when unidentified.</summary>
    public string? MatchMethod { get; set; }

    /// <summary>Each source's structured specs feeding the merge (brand site, store listing, …).</summary>
    public List<SpecSource> RawSpecsBySource { get; } = new();

    /// <summary>The canonical, merged-and-cleaned spec sheet, or null until the merge step runs.</summary>
    public IReadOnlyDictionary<string, string>? MergedSpecs { get; set; }

    /// <summary>R2 URLs of the raw per-source spec blobs persisted under <c>site-data/</c>.</summary>
    public List<string> RawSpecsR2Urls { get; } = new();

    /// <summary>R2 URL of the canonical spec sheet under <c>final-specs/</c>, when stored.</summary>
    public string? FinalSpecsR2Url { get; set; }
}
