namespace Daleel.Web.Data;

/// <summary>
/// An admin-only audit record of the halal VISION screen's verdict on ONE product/brand image — the
/// full picture of "every image and whether it was shown, hidden, or could not be screened, and why".
/// Unlike <see cref="FilteredContentLog"/> (which records only FLAGGED findings, for threshold tuning),
/// this logs EVERY candidate image the <c>ImageCheckHandler</c> screened, including the clean ones that
/// were shown — so an admin can see exactly what the filter did to each photo.
/// </summary>
/// <remarks>
/// Anonymous by construction (no userId): image review is about the content and the model's decision,
/// not who searched. Keyed by (SearchJobId, ImageUrl) so a re-screen (e.g. after an infra outage
/// clears) UPDATES the row rather than duplicating it — the log reflects the latest verdict.
/// </remarks>
public sealed class ImageModerationLog
{
    public long Id { get; set; }

    /// <summary>The search whose results carried this image.</summary>
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

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>The three outcomes an image screen can record. Kept as constants so the handler, the
/// repository and the admin page agree on the exact strings.</summary>
public static class ImageModerationDecision
{
    public const string Shown = "shown";
    public const string Hidden = "hidden";
    public const string Unscreened = "unscreened";
}
