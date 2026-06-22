namespace Daleel.Core.Models;

/// <summary>
/// A single customer opinion extracted (by the LLM) from a social post or forum thread,
/// reduced to a structured shape so opinions from many sources can be aggregated.
/// </summary>
public record CustomerOpinion
{
    /// <summary>The product or brand the opinion is about.</summary>
    public string Subject { get; init; } = string.Empty;

    /// <summary>Overall polarity.</summary>
    public Sentiment Sentiment { get; init; } = Sentiment.Neutral;

    /// <summary>Optional 1–5 rating if the opinion implies one.</summary>
    public double? Rating { get; init; }

    /// <summary>Positive points the author raised.</summary>
    public IReadOnlyList<string> Pros { get; init; } = Array.Empty<string>();

    /// <summary>Negative points the author raised.</summary>
    public IReadOnlyList<string> Cons { get; init; } = Array.Empty<string>();

    /// <summary>A short quote or paraphrase capturing the opinion.</summary>
    public string? Excerpt { get; init; }

    /// <summary>Where it came from (platform, group name, forum, URL).</summary>
    public string? Source { get; init; }

    /// <summary>When the opinion was posted, if known.</summary>
    public DateTimeOffset? Date { get; init; }

    /// <summary>BCP-47 language of the original text, e.g. "ar" or "en".</summary>
    public string? Language { get; init; }
}
