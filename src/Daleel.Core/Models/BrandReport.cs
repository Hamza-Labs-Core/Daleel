namespace Daleel.Core.Models;

/// <summary>
/// The aggregated brand-intelligence report produced by the agent for a brand in a
/// given market.
/// </summary>
public record BrandReport
{
    public string Brand { get; init; } = string.Empty;

    /// <summary>Market key the report covers, e.g. "jordan".</summary>
    public string Geo { get; init; } = string.Empty;

    /// <summary>LLM executive summary of the brand's presence and reception.</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>Physical/online stores found for the brand.</summary>
    public IReadOnlyList<StoreResult> Stores { get; init; } = Array.Empty<StoreResult>();

    /// <summary>Places-enriched store locations, when a places lookup ran.</summary>
    public IReadOnlyList<StoreLocation> Locations { get; init; } = Array.Empty<StoreLocation>();

    /// <summary>Current deals/promotions found.</summary>
    public IReadOnlyList<DealResult> Deals { get; init; } = Array.Empty<DealResult>();

    /// <summary>Social-media sentiment breakdown.</summary>
    public SentimentSummary Sentiment { get; init; } = new();

    /// <summary>Competitors that surfaced during research.</summary>
    public IReadOnlyList<CompetitorMention> Competitors { get; init; } = Array.Empty<CompetitorMention>();

    /// <summary>Source URLs / references the report drew on.</summary>
    public IReadOnlyList<string> Sources { get; init; } = Array.Empty<string>();

    /// <summary>When the report was generated (stamped by the caller).</summary>
    public DateTimeOffset? GeneratedAt { get; init; }
}
