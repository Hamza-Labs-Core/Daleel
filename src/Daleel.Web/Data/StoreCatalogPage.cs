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
}
