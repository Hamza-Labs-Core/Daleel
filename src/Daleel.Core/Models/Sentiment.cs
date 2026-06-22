namespace Daleel.Core.Models;

/// <summary>Coarse sentiment polarity assigned to an opinion or mention.</summary>
public enum Sentiment
{
    Negative = -1,
    Neutral = 0,
    Positive = 1
}

/// <summary>
/// Aggregated sentiment across many social/forum mentions of a brand or product.
/// </summary>
public record SentimentSummary
{
    public int PositiveCount { get; init; }
    public int NeutralCount { get; init; }
    public int NegativeCount { get; init; }

    /// <summary>Total mentions considered.</summary>
    public int Total => PositiveCount + NeutralCount + NegativeCount;

    /// <summary>
    /// Net sentiment score in [-1, 1]: (positive − negative) / total. Zero when there
    /// are no mentions.
    /// </summary>
    public double NetScore => Total == 0 ? 0.0 : (double)(PositiveCount - NegativeCount) / Total;

    /// <summary>A one-line human label derived from <see cref="NetScore"/>.</summary>
    public string Label => NetScore switch
    {
        >= 0.4 => "mostly positive",
        >= 0.1 => "leaning positive",
        > -0.1 => "mixed",
        > -0.4 => "leaning negative",
        _ => "mostly negative"
    };

    /// <summary>A short LLM- or rule-generated prose summary of the sentiment.</summary>
    public string? Summary { get; init; }

    /// <summary>Builds a summary by tallying a sequence of sentiments.</summary>
    public static SentimentSummary FromOpinions(IEnumerable<Sentiment> sentiments)
    {
        int pos = 0, neu = 0, neg = 0;
        foreach (var s in sentiments)
        {
            switch (s)
            {
                case Sentiment.Positive: pos++; break;
                case Sentiment.Negative: neg++; break;
                default: neu++; break;
            }
        }

        return new SentimentSummary { PositiveCount = pos, NeutralCount = neu, NegativeCount = neg };
    }
}
