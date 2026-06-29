namespace Daleel.Core.Models;

/// <summary>
/// The kind of intelligence task the user is asking for. The agent classifies a
/// free-form question into one of these to pick a research strategy and report shape.
/// </summary>
public enum QueryType
{
    /// <summary>"best AC in Jordan" — research a product category.</summary>
    ProductResearch,

    /// <summary>"McDonald's in Jordan" — research a specific brand.</summary>
    BrandLookup,

    /// <summary>"where to buy Nike in Amman" — find stores.</summary>
    StoreFinder,

    /// <summary>"Samsung deals in Jordan" — find promotions.</summary>
    DealHunter,

    /// <summary>"what do people think of X" — aggregate opinions.</summary>
    OpinionAggregation,

    /// <summary>"Samsung vs LG" — head-to-head comparison.</summary>
    Comparison,

    /// <summary>Anything that doesn't fit the above.</summary>
    General
}

/// <summary>
/// The research plan the LLM produces from a query: classified type plus the concrete
/// searches to run, in both market languages. This is the contract between the
/// "planner" LLM call and the search-execution stage.
/// </summary>
public record SearchStrategy
{
    /// <summary>The classified query type (task shape).</summary>
    public QueryType QueryType { get; init; } = QueryType.General;

    /// <summary>
    /// The classified intent — what KIND of thing the user wants (product / service / place).
    /// Orthogonal to <see cref="QueryType"/>; drives which extraction prompt runs. Defaults to
    /// <see cref="SearchIntentType.Product"/> when the planner can't tell.
    /// </summary>
    public SearchIntentType Intent { get; init; } = SearchIntentType.Product;

    /// <summary>The product/brand/subject the query is about, as the LLM understood it.</summary>
    public string Subject { get; init; } = string.Empty;

    /// <summary>Web search queries (mixed Arabic + English) to run.</summary>
    public IReadOnlyList<string> WebQueries { get; init; } = Array.Empty<string>();

    /// <summary>Shopping/marketplace search queries.</summary>
    public IReadOnlyList<string> ShoppingQueries { get; init; } = Array.Empty<string>();

    /// <summary>Social-platform search queries / keywords.</summary>
    public IReadOnlyList<string> SocialQueries { get; init; } = Array.Empty<string>();

    /// <summary>Google-Places-style store queries (e.g. "AC stores", "متاجر مكيفات").</summary>
    public IReadOnlyList<string> PlacesQueries { get; init; } = Array.Empty<string>();

    /// <summary>Specific URLs the LLM wants deep-read (routed through a scrape provider).</summary>
    public IReadOnlyList<string> UrlsToRead { get; init; } = Array.Empty<string>();

    /// <summary>The LLM's short rationale for this plan (for transparency/logging).</summary>
    public string? Reasoning { get; init; }
}
