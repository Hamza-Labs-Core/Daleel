using Daleel.Core.Models;

namespace Daleel.Web.Pipeline.SubWorkflows;

/// <summary>
/// Per-product state for <see cref="ProductDetailWorkflow"/> — the LLM extractor for a single product detail
/// page. Seeded with the product's listing-level summary (which carries its detail URL); the activities
/// render that page, extract the full record, fold it onto <see cref="Result"/>, and persist it. Dispatched
/// as a fan-out target (one per discovered product) by the store and brand crawlers, and usable standalone.
/// Holds only serializable data — the live agent + progress sink come from <see cref="SubWorkflowServices"/>.
/// </summary>
public sealed class ProductDetailState : SubWorkflowState
{
    // ── Inputs ───────────────────────────────────────────────────────────────────

    /// <summary>The product as discovered at listing level (input); its <see cref="ProductListing.Url"/> is the detail page.</summary>
    public ProductListing Listing { get; set; } = new();

    /// <summary>The shopper's original query — used to tag the persisted entity.</summary>
    public string Query { get; set; } = string.Empty;

    /// <summary>Display name of the source store/brand (used as the offer source).</summary>
    public string SiteName { get; set; } = string.Empty;

    // ── Working / outputs ────────────────────────────────────────────────────────

    /// <summary>The listing enriched with the detail page's data (output; starts equal to <see cref="Listing"/>).</summary>
    public ProductListing Result { get; set; } = new();

    /// <summary>The rich detail record the LLM extracted (all images, specs, reviews, related), or null on failure.</summary>
    public ProductDetail? Detail { get; set; }

    /// <summary>True once the product was persisted as an EntityDocument.</summary>
    public bool Persisted { get; set; }
}
