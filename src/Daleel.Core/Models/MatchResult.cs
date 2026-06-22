namespace Daleel.Core.Models;

/// <summary>
/// The mode used to compare a keyword against post text.
/// </summary>
public enum MatchMode
{
    /// <summary>Normalized keyword must equal the entire normalized text.</summary>
    Exact,

    /// <summary>Normalized text must contain the normalized keyword as a substring.</summary>
    Contains,

    /// <summary>
    /// Token-level fuzzy match: a keyword matches if any text token is within an
    /// edit-distance threshold (Levenshtein) of the keyword's normalized form.
    /// </summary>
    Fuzzy
}

/// <summary>
/// Outcome of matching a set of keywords against a single post.
/// </summary>
public record MatchResult
{
    /// <summary>Whether any keyword matched.</summary>
    public bool IsMatch { get; init; }

    /// <summary>
    /// Confidence in the range [0, 1]. Exact/contains hits score 1.0; fuzzy hits
    /// score by similarity (1 − normalizedEditDistance).
    /// </summary>
    public double Score { get; init; }

    /// <summary>The keyword that produced the (best) match, if any.</summary>
    public string? MatchedKeyword { get; init; }

    /// <summary>The matching mode that produced the hit.</summary>
    public MatchMode Mode { get; init; }

    /// <summary>
    /// A short snippet of the original text around the match, for human review.
    /// Null when there was no match or the source text was empty.
    /// </summary>
    public string? Context { get; init; }

    /// <summary>A canonical "no match" result.</summary>
    public static MatchResult NoMatch { get; } = new() { IsMatch = false, Score = 0.0 };
}
