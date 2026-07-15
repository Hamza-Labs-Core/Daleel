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

    // ── Structured query metadata (the "search object" fields) ─────────────────
    // All optional with empty defaults: a planner that omits them leaves the grid on its generic
    // behavior, and old persisted ResultJson (which lacks them) deserializes unchanged.

    /// <summary>The actual product the user wants ("diapers"), distinct from the free-text Subject.</summary>
    public string Product { get; init; } = string.Empty;

    /// <summary>Constraints stated IN the query ("size"→"4", "color"→"white") — not per-result specs.</summary>
    public IReadOnlyDictionary<string, string> Specs { get; init; } = new Dictionary<string, string>();

    /// <summary>The place/market scope the user named ("Amman"), when any.</summary>
    public string Location { get; init; } = string.Empty;

    /// <summary>The user's goal as free text ("cheapest", "best for newborns"). Guides ranking.</summary>
    public string Goal { get; init; } = string.Empty;

    /// <summary>Filter dimensions relevant to this product type, named by the planner.</summary>
    public IReadOnlyList<SearchFacet> Facets { get; init; } = Array.Empty<SearchFacet>();

    /// <summary>The goal-driven default sort key ("price_asc", "rating", …); empty ⇒ resolver heuristics.</summary>
    public string DefaultSort { get; init; } = string.Empty;
}

/// <summary>
/// One product-type-specific filter dimension for the result grid, named by the planner
/// ("Screen Size" for TVs, "Size" for diapers). <see cref="Key"/> binds to a
/// <c>ProductModel.Specs</c> key; <see cref="Values"/> are optional candidate options that keep
/// the facet useful when results carry sparse specs (options = union of these + result values).
/// </summary>
public record SearchFacet
{
    /// <summary>The spec key this facet filters on — binds to a <c>ProductModel.Specs</c> key.</summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>Human-readable display label for the facet ("Screen Size").</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>Optional unit hint for the dimension ("inch", "kg"); null when unitless.</summary>
    public string? Unit { get; init; }

    /// <summary>Planner-supplied candidate options; the grid merges these with values seen in results.</summary>
    public IReadOnlyList<string> Values { get; init; } = Array.Empty<string>();
}
