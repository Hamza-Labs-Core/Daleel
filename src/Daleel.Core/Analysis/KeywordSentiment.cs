using Daleel.Core.Arabic;
using Daleel.Core.Models;

namespace Daleel.Core.Analysis;

/// <summary>
/// A lightweight bilingual (Arabic + English) keyword sentiment scorer. Used as a fast fallback
/// when no LLM opinion pass runs — both the agent's brand-sentiment summary and the dashboard's
/// social-feed monitor share this single implementation so they always agree.
/// </summary>
/// <remarks>
/// Text is run through <see cref="ArabicNormalizer"/> so Arabic orthographic variants all match,
/// then positive and negative cue words are counted; the larger tally wins (ties → neutral).
/// </remarks>
public static class KeywordSentiment
{
    private static readonly string[] Positive =
        { "ممتاز", "رائع", "جيد", "افضل", "احسن", "good", "great", "best", "excellent", "love" };

    private static readonly string[] Negative =
        { "سيء", "رديء", "مشكله", "خربان", "bad", "worst", "broken", "problem", "hate" };

    // Pre-normalize the cue words once (they're static) so scoring only normalizes the input text.
    private static readonly string[] NormalizedPositive =
        Array.ConvertAll(Positive, ArabicNormalizer.Normalize);

    private static readonly string[] NormalizedNegative =
        Array.ConvertAll(Negative, ArabicNormalizer.Normalize);

    /// <summary>Scores a single piece of text as Positive, Negative, or Neutral.</summary>
    public static Sentiment Score(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Sentiment.Neutral;
        }

        var n = ArabicNormalizer.Normalize(text);
        var pos = NormalizedPositive.Count(w => n.Contains(w, StringComparison.Ordinal));
        var neg = NormalizedNegative.Count(w => n.Contains(w, StringComparison.Ordinal));
        return pos > neg ? Sentiment.Positive : neg > pos ? Sentiment.Negative : Sentiment.Neutral;
    }
}
