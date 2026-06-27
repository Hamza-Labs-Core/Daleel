using Daleel.Core.Intelligence;

namespace Daleel.Core.Models;

/// <summary>
/// A recommended product within a <see cref="ProductIntelligence"/> report, distilled
/// from aggregated opinions and prices.
/// </summary>
public record ProductRecommendation
{
    public string Name { get; init; } = string.Empty;
    public int Rank { get; init; }

    /// <summary>Aggregated pros across opinions.</summary>
    public IReadOnlyList<string> Pros { get; init; } = Array.Empty<string>();

    /// <summary>Aggregated cons across opinions.</summary>
    public IReadOnlyList<string> Cons { get; init; } = Array.Empty<string>();

    /// <summary>Number of distinct mentions backing this recommendation.</summary>
    public int MentionCount { get; init; }

    /// <summary>Sentiment toward this specific product.</summary>
    public SentimentSummary Sentiment { get; init; } = new();

    /// <summary>Observed price range (lowest and highest price points found).</summary>
    public Money? LowestPrice { get; init; }
    public Money? HighestPrice { get; init; }

    /// <summary>Where to buy it (store names / links).</summary>
    public IReadOnlyList<string> WhereToBuy { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Full product-category research report: ranked recommendations, prices, deals, and an
/// LLM narrative. Produced for queries like "best AC in Jordan".
/// </summary>
public record ProductIntelligence
{
    /// <summary>The product category researched, e.g. "split AC".</summary>
    public string Category { get; init; } = string.Empty;

    public string Geo { get; init; } = string.Empty;

    /// <summary>LLM narrative answering the user's question.</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>Top recommendations, best first.</summary>
    public IReadOnlyList<ProductRecommendation> TopProducts { get; init; } = Array.Empty<ProductRecommendation>();

    /// <summary>All collected price points (across products and stores).</summary>
    public IReadOnlyList<PricePoint> Prices { get; init; } = Array.Empty<PricePoint>();

    /// <summary>Best current deals, ranked.</summary>
    public IReadOnlyList<DealListing> Deals { get; init; } = Array.Empty<DealListing>();

    /// <summary>Raw opinions the analysis drew on.</summary>
    public IReadOnlyList<CustomerOpinion> Opinions { get; init; } = Array.Empty<CustomerOpinion>();

    /// <summary>Stores worth buying from (Places-enriched).</summary>
    public IReadOnlyList<StoreLocation> Stores { get; init; } = Array.Empty<StoreLocation>();

    public IReadOnlyList<string> Sources { get; init; } = Array.Empty<string>();
    public DateTimeOffset? GeneratedAt { get; init; }
}

/// <summary>
/// A head-to-head comparison of two (or more) products in a market.
/// </summary>
public record ProductComparison
{
    public IReadOnlyList<string> Products { get; init; } = Array.Empty<string>();
    public string Geo { get; init; } = string.Empty;

    /// <summary>LLM verdict / summary of the comparison.</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>Per-product breakdown keyed by product name.</summary>
    public IReadOnlyList<ProductRecommendation> Breakdown { get; init; } = Array.Empty<ProductRecommendation>();

    /// <summary>The product the analysis recommends overall, if any.</summary>
    public string? Winner { get; init; }

    /// <summary>
    /// The product-type-aware schema (BTU/energy for ACs, RAM/storage for phones…) the LLM
    /// determined for the compared products, so the compare page can surface the dimensions that
    /// actually decide this category. Empty for unclassifiable comparisons.
    /// </summary>
    public ProductSchema Schema { get; init; } = ProductSchema.General;

    public IReadOnlyList<string> Sources { get; init; } = Array.Empty<string>();
    public DateTimeOffset? GeneratedAt { get; init; }
}
