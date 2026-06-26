using Daleel.Core.Models;

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
}
