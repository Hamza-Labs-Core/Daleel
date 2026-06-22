namespace Daleel.Core.Models;

/// <summary>
/// Platform-agnostic representation of a single social media post. Every fetcher
/// (Apify actor, file import, etc.) maps its source-specific JSON into this shape
/// so the rest of the pipeline never has to know where a post came from.
/// </summary>
public record SocialPost
{
    /// <summary>Stable identifier from the source platform (post id / permalink id).</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Raw post text exactly as fetched, before any normalization.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>Display name or handle of the author, when available.</summary>
    public string? Author { get; init; }

    /// <summary>Publication time in UTC, when the source provides it.</summary>
    public DateTimeOffset? Timestamp { get; init; }

    /// <summary>Canonical URL of the post.</summary>
    public string? Url { get; init; }

    /// <summary>Identifier of the <see cref="Source"/> that produced this post.</summary>
    public string? Source { get; init; }

    /// <summary>Total reaction / like count, when available.</summary>
    public int? Reactions { get; init; }

    /// <summary>
    /// Free-form extra fields preserved from the original payload that don't have a
    /// first-class slot above. Useful for debugging actor output.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Extra { get; init; }
}
