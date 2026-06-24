namespace Daleel.Core.Models;

/// <summary>
/// A real user opinion pulled from social media or a forum, with the actual text (translated
/// to the reader's language where needed), its source, and polarity. The "social proof" layer
/// — what real people in the market say, beyond official specs and prices.
/// </summary>
public record UserReview
{
    /// <summary>The user's words (translated if the original wasn't in the reader's language).</summary>
    public string Quote { get; init; } = string.Empty;

    /// <summary>The original-language text, when a translation was provided in <see cref="Quote"/>.</summary>
    public string? OriginalText { get; init; }

    /// <summary>Platform / author / group the opinion came from, e.g. "Facebook group".</summary>
    public string? Source { get; init; }

    /// <summary>Link to the original post, when available.</summary>
    public string? Url { get; init; }

    public Sentiment Sentiment { get; init; } = Sentiment.Neutral;

    /// <summary>When it was posted (recent opinions matter more), if known.</summary>
    public DateTimeOffset? Date { get; init; }

    /// <summary>BCP-47 language of the original text, e.g. "ar".</summary>
    public string? Language { get; init; }
}

/// <summary>
/// Aggregated social proof for a brand/product: real user quotes plus a sentiment breakdown.
/// </summary>
public record SocialProof
{
    public IReadOnlyList<UserReview> Reviews { get; init; } = Array.Empty<UserReview>();

    public int Positive => Reviews.Count(r => r.Sentiment == Sentiment.Positive);
    public int Negative => Reviews.Count(r => r.Sentiment == Sentiment.Negative);
    public int Neutral => Reviews.Count(r => r.Sentiment == Sentiment.Neutral);

    public bool HasReviews => Reviews.Count > 0;

    /// <summary>Sentiment tally as a <see cref="SentimentSummary"/> (for the shared label/score logic).</summary>
    public SentimentSummary Summary => SentimentSummary.FromOpinions(Reviews.Select(r => r.Sentiment));
}
