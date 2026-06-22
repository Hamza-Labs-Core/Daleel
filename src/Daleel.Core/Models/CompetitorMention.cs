namespace Daleel.Core.Models;

/// <summary>
/// A reference to a competitor surfaced while researching a brand — used for
/// competitive intelligence in <see cref="BrandReport"/>.
/// </summary>
public record CompetitorMention
{
    /// <summary>The competing brand's name.</summary>
    public string Competitor { get; init; } = string.Empty;

    /// <summary>How often the competitor was mentioned alongside the subject.</summary>
    public int MentionCount { get; init; }

    /// <summary>Aggregate sentiment toward the competitor, when assessed.</summary>
    public Sentiment Sentiment { get; init; } = Sentiment.Neutral;

    /// <summary>A short note on how/why the competitor came up.</summary>
    public string? Context { get; init; }
}
