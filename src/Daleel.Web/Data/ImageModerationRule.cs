namespace Daleel.Web.Data;

/// <summary>
/// One admin-managed halal image-moderation rule: a category tag and the instruction the vision model
/// applies for it. The active rules are composed into the screening prompt ("the following rules apply:
/// …") — see <see cref="Daleel.Web.Moderation.VisionPolicy"/> — so the whole image policy is a LIST an
/// admin edits on /admin/moderation, nothing hardcoded. Seeded from the built-in defaults on first run.
/// </summary>
public sealed class ImageModerationRule
{
    public int Id { get; set; }

    /// <summary>Short category tag the model flags with, e.g. "immodest", "alcohol". Lower-cased.</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>The instruction the model applies for this rule (what to flag, with any nuances).</summary>
    public string Instruction { get; set; } = string.Empty;

    /// <summary>Disabled rules are kept for reference but excluded from the composed prompt.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Order the rules appear in the prompt and the admin list.</summary>
    public int SortOrder { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
