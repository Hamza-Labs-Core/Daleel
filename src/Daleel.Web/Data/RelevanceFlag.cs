using System.Text.RegularExpressions;

namespace Daleel.Web.Data;

/// <summary>
/// A user's "not relevant" flag on a search result item — the raw signal the relevance learning loop
/// consumes ("searched pants, got socks → flag it"). Identity is captured by the SAME keys the grid uses
/// (<see cref="DedupKey"/> + <see cref="StableId"/>) plus a raw brand/model/name snapshot, so the loop can
/// re-derive the key even if normalization changes. <see cref="UserHash"/> is a one-way hash (never the raw id).
/// </summary>
public sealed class RelevanceFlag
{
    public long Id { get; set; }

    /// <summary>One-way hash of the flagging user's id (never the raw id). Null for anonymous.</summary>
    public string? UserHash { get; set; }

    /// <summary>The raw query the user searched.</summary>
    public string Query { get; set; } = string.Empty;

    /// <summary>Normalized query key for matching future searches of the same thing.</summary>
    public string QueryKey { get; set; } = string.Empty;

    /// <summary>The relevance target the gate judges against (the product type, e.g. "pants").</summary>
    public string? Target { get; set; }

    /// <summary>Market the search ran in.</summary>
    public string? Geo { get; set; }

    /// <summary>The grid dedup key of the flagged item (brand+model, else name) — how the loop matches items.</summary>
    public string DedupKey { get; set; } = string.Empty;

    /// <summary>The routing id of the flagged item.</summary>
    public string? StableId { get; set; }

    /// <summary>Raw brand snapshot (pre-normalization), so keys can be re-derived if normalization changes.</summary>
    public string? Brand { get; set; }

    /// <summary>Raw model snapshot.</summary>
    public string? Model { get; set; }

    /// <summary>Raw name snapshot.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional free-text reason the user gave.</summary>
    public string? Reason { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Normalizes a query into a stable match key (lowercase, trimmed, collapsed whitespace).</summary>
    public static string QueryKeyOf(string? query) =>
        string.IsNullOrWhiteSpace(query)
            ? string.Empty
            : Regex.Replace(query.Trim().ToLowerInvariant(), @"\s+", " ");
}
