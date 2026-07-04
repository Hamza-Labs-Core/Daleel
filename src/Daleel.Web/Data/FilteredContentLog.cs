using Daleel.Core.Moderation;

namespace Daleel.Web.Data;

/// <summary>
/// An admin-only audit record of one moderation finding: exactly what was flagged (text snippet
/// and/or image), where on the item it was found, where it came from, how it was decided
/// (keyword rule, LLM, or vision — with confidence), and whether the item was removed or only
/// its image stripped. Carries the admin's feedback (correct/incorrect rating, whitelist state)
/// so the filter can be reviewed AND corrected from /admin/filtered.
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

    /// <summary>The keyword that matched or the model's short reason — lets admins tune the filter.</summary>
    public string? Rule { get; set; }

    /// <summary>Kind of item filtered: "text", "SearchResult", "StoreLocation", …</summary>
    public string? Kind { get; set; }

    /// <summary>Truncated snippet of the offending content (admin review only).</summary>
    public string? Content { get; set; }

    /// <summary>Which projected field the match was found in: "title", "snippet", "seller", "image", …</summary>
    public string? Field { get; set; }

    /// <summary>Link to the source page the flagged item came from.</summary>
    public string? SourceUrl { get; set; }

    /// <summary>The flagged (or stripped) image, shown as a thumbnail in the admin detail view.</summary>
    public string? ImageUrl { get; set; }

    /// <summary>Classifier confidence (1.0 for deterministic keyword matches).</summary>
    public double Confidence { get; set; } = 1.0;

    /// <summary>How the finding was decided: "keyword", "llm", or "vision".</summary>
    public string? DecisionSource { get; set; }

    /// <summary>Stable hash of the item's filterable text — the whitelist key for "this content is fine".</summary>
    public string? ContentHash { get; set; }

    /// <summary>True when the whole item was removed; false when only its image was stripped.</summary>
    public bool ItemRemoved { get; set; } = true;

    /// <summary>Admin verdict: +1 the content IS haram (flag right), -1 halal (flag wrong). Drives thresholds.</summary>
    public int? Rating { get; set; }

    /// <summary>When the admin rated this finding.</summary>
    public DateTimeOffset? RatedAt { get; set; }

    /// <summary>
    /// The LLM auto-reviewer's verdict, same scale as <see cref="Rating"/>. Feeds thresholds only
    /// when no admin rating exists — the human always overrides the machine.
    /// </summary>
    public int? AutoRating { get; set; }

    /// <summary>When the auto-reviewer audited this finding (null = not yet reviewed).</summary>
    public DateTimeOffset? AutoReviewedAt { get; set; }

    /// <summary>The auto-reviewer's one-line justification (admin review surface).</summary>
    public string? AutoReviewNote { get; set; }

    /// <summary>The whitelist entry created from this finding via the admin "undo" action, if any.</summary>
    public long? WhitelistEntryId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Maps a pipeline finding to a persistable row, truncating every free-form value to its
    /// column limit. Model reasons and scraped URLs are unbounded, the batch insert is
    /// all-or-nothing, and the callers deliberately swallow persistence errors — so ONE over-long
    /// value would otherwise silently discard the run's entire findings batch.
    /// </summary>
    public static FilteredContentLog From(FilterFinding d, string? query, string? geo, DateTimeOffset now) => new()
    {
        Query = Truncate(query, 2000),
        Geo = Truncate(geo, 64),
        Category = Truncate(d.Category, 32)!,
        Rule = Truncate(d.Rule, 256),
        Kind = Truncate(d.Kind, 64),
        Content = Truncate(d.Content, 300),
        Field = Truncate(d.Field, 32),
        SourceUrl = Truncate(d.SourceUrl, 2048),
        ImageUrl = Truncate(d.ImageUrl, 2048),
        Confidence = d.Confidence,
        DecisionSource = d.Source.ToString().ToLowerInvariant(),
        ContentHash = Truncate(d.ContentHash, 64),
        ItemRemoved = d.ItemRemoved,
        CreatedAt = now
    };

    private static string? Truncate(string? s, int max) =>
        s is null || s.Length <= max ? s : s[..max];
}

/// <summary>
/// One dynamic keyword-rule adjustment, persisted so the filter can learn without a deploy.
/// The LLM auto-reviewer proposes them (suppressions activate on repeated-consensus, additions
/// wait as "pending" for admin approval); admins can create, approve, or revoke any of them.
/// The active set rides into every search run via the moderation policy snapshot.
/// </summary>
public sealed class ModerationRuleOverride
{
    public long Id { get; set; }

    /// <summary>"suppress-term" or "add-term" (see Core's ModerationRule).</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Blocklist category, e.g. "alcohol".</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>The trigger term (suppressions may carry the matched article prefix).</summary>
    public string Term { get; set; } = string.Empty;

    /// <summary>"en" or "ar".</summary>
    public string Language { get; set; } = string.Empty;

    /// <summary>Why this rule exists — the reviewer's aggregated evidence or the admin's note.</summary>
    public string? Reason { get; set; }

    /// <summary>"llm" or "admin".</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>"active", "pending" (awaiting admin approval), or "revoked".</summary>
    public string Status { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the rule was approved or revoked.</summary>
    public DateTimeOffset? ResolvedAt { get; set; }
}

/// <summary>
/// One admin-created whitelist entry: a stable key (source URL, image URL, or content hash) that
/// the moderation pipeline must never filter again. Created from a finding via the admin "undo"
/// action; deleting the entry re-enables filtering.
/// </summary>
public sealed class ModerationWhitelistEntry
{
    public long Id { get; set; }

    /// <summary>The whitelisted key — a source URL, image URL, or content hash.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>What the key is: "url", "image", or "hash".</summary>
    public string MatchType { get; set; } = string.Empty;

    /// <summary>Category of the finding this entry undid (for admin context).</summary>
    public string? Category { get; set; }

    /// <summary>Snippet of the un-filtered content, so the whitelist stays reviewable.</summary>
    public string? Note { get; set; }

    /// <summary>The finding this entry was created from, when applicable.</summary>
    public long? SourceLogId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
