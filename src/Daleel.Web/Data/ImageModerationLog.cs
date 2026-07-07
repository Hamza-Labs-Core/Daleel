namespace Daleel.Web.Data;

/// <summary>
/// The system-wide REGISTRY of every product/brand image the halal vision screen has looked at — one row
/// per distinct image URL, carrying its LATEST verdict ("every image and whether it was shown, hidden, or
/// could not be screened, and why"). Unlike <see cref="FilteredContentLog"/> (which records only FLAGGED
/// findings, for threshold tuning), this tracks EVERY candidate image, clean ones included, so an admin
/// can review the whole image corpus on /admin/images and flag any/all for re-evaluation.
/// </summary>
/// <remarks>
/// Anonymous by construction (no userId): image review is about the content and the model's decision, not
/// who searched. Keyed by <see cref="ImageUrl"/> (unique) so re-seeing / re-screening an image UPDATES its
/// single registry row rather than duplicating it; <see cref="SearchJobId"/>/<see cref="Query"/> record
/// where it was LAST seen. <see cref="ReEvalRequestedAt"/> is the re-evaluation queue marker.
/// </remarks>
public sealed class ImageModerationLog
{
    public long Id { get; set; }

    /// <summary>The search whose results LAST carried this image.</summary>
    public int? SearchJobId { get; set; }

    /// <summary>The query that surfaced the image, e.g. "women pants".</summary>
    public string? Query { get; set; }

    /// <summary>Market the search ran in, e.g. "usa".</summary>
    public string? Geo { get; set; }

    /// <summary>The image that was screened.</summary>
    public string ImageUrl { get; set; } = string.Empty;

    /// <summary>The product model name or brand the image belongs to (admin context).</summary>
    public string? ItemName { get; set; }

    /// <summary>What the image is attached to: "product" or "brand-logo".</summary>
    public string? ItemKind { get; set; }

    /// <summary>
    /// The screen's outcome for this image: "shown" (verified clean), "hidden" (flagged haram), or
    /// "unscreened" (the vision screen could not run — held hidden until it recovers, fail-closed).
    /// </summary>
    public string Decision { get; set; } = ImageModerationDecision.Shown;

    /// <summary>Haram category when hidden (immodest / alcohol / pork / …); null when shown/unscreened.</summary>
    public string? Category { get; set; }

    /// <summary>Vision confidence 0–1 for a flagged image; null when shown/unscreened.</summary>
    public double? Score { get; set; }

    /// <summary>The model's short reason for flagging; null when shown/unscreened.</summary>
    public string? Reason { get; set; }

    /// <summary>How the decision was reached: "vision" (screened), or "not-configured" (no vision model).</summary>
    public string? DecisionSource { get; set; }

    /// <summary>When this image was last screened (the verdict's timestamp).</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// The re-evaluation QUEUE marker: non-null means an admin flagged this image to be re-screened with
    /// the current rules. The <c>ImageReEvalService</c> drains flagged rows (oldest first), re-runs the
    /// vision screen (bypassing the verdict cache), writes the fresh verdict and clears this back to null.
    /// </summary>
    public DateTimeOffset? ReEvalRequestedAt { get; set; }
}

/// <summary>The three outcomes an image screen can record. Kept as constants so the handler, the
/// repository and the admin page agree on the exact strings.</summary>
public static class ImageModerationDecision
{
    public const string Shown = "shown";
    public const string Hidden = "hidden";
    public const string Unscreened = "unscreened";
}
