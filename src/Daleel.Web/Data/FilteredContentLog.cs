namespace Daleel.Web.Data;

/// <summary>
/// An admin-only audit record of one piece of content the halal filter removed: what was
/// filtered, which blocklist rule fired, and the query that produced it — so an admin can verify
/// the filter isn't too aggressive (or missing things).
/// </summary>
/// <remarks>
/// Deliberately carries NO userId. Filter review is about the content and the rules, not who
/// searched, so this table stays anonymous by construction.
/// </remarks>
public sealed class FilteredContentLog
{
    public long Id { get; set; }

    /// <summary>The search query that surfaced the filtered content.</summary>
    public string? Query { get; set; }

    /// <summary>Market the search ran in, e.g. "jordan".</summary>
    public string? Geo { get; set; }

    /// <summary>Blocked category, e.g. "alcohol".</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>The exact blocklist term that matched, e.g. "bar" — lets admins tune the list.</summary>
    public string? Rule { get; set; }

    /// <summary>Kind of item filtered: "text", "SearchResult", "StoreLocation", …</summary>
    public string? Kind { get; set; }

    /// <summary>Truncated snippet of the offending content/URL (admin review only).</summary>
    public string? Content { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
