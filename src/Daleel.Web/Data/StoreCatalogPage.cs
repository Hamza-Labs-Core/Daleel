using System.ComponentModel.DataAnnotations;

namespace Daleel.Web.Data;

/// <summary>
/// The inventory monitor's change detector: one row per catalogue page URL with the hash of its
/// last-seen content. An unchanged page is skipped entirely (no parsing, no LLM), which is what
/// keeps a sync's recurring cost proportional to what CHANGED rather than to catalogue size.
/// </summary>
public sealed class StoreCatalogPage
{
    [Key]
    public int Id { get; set; }

    /// <summary>Bare registrable host, lower-case (the store's domain).</summary>
    public string Domain { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    /// <summary>SHA-256 (hex) of the page payload as last synced.</summary>
    public string ContentHash { get; set; } = string.Empty;

    public DateTimeOffset LastSeenAt { get; set; }

    /// <summary>
    /// JSON array of the product keys last extracted from this page (HTML mode). An UNCHANGED page's
    /// products must still advance the presence watermark — without re-running the LLM — and this
    /// remembered set is how: hash-skip stamps <c>ScrapedPrice.LastSeenAt</c> for exactly these keys.
    /// Null for machine-readable catalogues (Shopify/Woo), whose parse is free and re-runs every sync.
    /// </summary>
    public string? ProductKeysJson { get; set; }

    /// <summary>
    /// The next-page URL last detected on this page (HTML mode), so an unchanged page's pagination
    /// walk continues without an LLM call. Null when the page was the last of its listing.
    /// </summary>
    public string? NextUrl { get; set; }
}
