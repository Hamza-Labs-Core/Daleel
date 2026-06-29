using Daleel.Core.Models;
using Daleel.Search.Abstractions;

namespace Daleel.Agent;

/// <summary>Tunables and ambient services for an <see cref="AgentService"/>.</summary>
public sealed class AgentOptions
{
    /// <summary>Default market when a query doesn't specify one.</summary>
    public string DefaultGeo { get; init; } = "usa";

    /// <summary>Max results to request per search query (places/social depth).</summary>
    public int ResultsPerQuery { get; init; } = 10;

    /// <summary>
    /// Depth for web/shopping queries: the provider pages through Google until it reaches this many
    /// results (SerpAPI caps at 10 pages). Defaults to a deep 100-result scan; total cost is still
    /// bounded by <see cref="MaxQueriesPerKind"/> and the per-job API-call cap.
    /// </summary>
    public int DeepResultsPerQuery { get; init; } = 100;

    /// <summary>Max web/shopping queries to actually execute from a plan (cost guard).</summary>
    /// <remarks>
    /// A broad query set is what gives "best &lt;category&gt;" searches their coverage — each diverse query
    /// (per-brand, per-tier, buying-guide) surfaces a different slice of the catalogue. Kept generous; total
    /// cost stays bounded by the per-job API-call cap.
    /// </remarks>
    public int MaxQueriesPerKind { get; init; } = 6;

    /// <summary>
    /// Max URLs to deep-read per run. For research-style queries the buying-guide / round-up articles ARE the
    /// product source, so reading more of them directly widens how many distinct models the extractor can
    /// surface; bounded by the per-job API-call cap.
    /// </summary>
    public int MaxUrlsToRead { get; init; } = 6;

    /// <summary>
    /// Max store/marketplace pages to deep-extract priced listings from per run. Higher than
    /// <see cref="MaxUrlsToRead"/> because Google Shopping coverage is thin in many markets, so real
    /// prices have to be scraped from the store pages themselves — going deeper here is what turns
    /// "Price on site" into actual prices. Cost stays bounded by the per-job API-call cap.
    /// </summary>
    public int MaxListingUrls { get; init; } = 12;

    /// <summary>
    /// BCP-47 language the analyst summary should be written in (e.g. "ar", "en"). Product and
    /// brand names are left untranslated. Defaults to English.
    /// </summary>
    public string Language { get; init; } = "en";

    /// <summary>Clock, injectable for deterministic tests.</summary>
    public Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.UtcNow;

    /// <summary>Optional progress logger.</summary>
    public Action<string>? Log { get; init; }
}

/// <summary>
/// Everything the agent gathered while executing a strategy, before analysis. The
/// specialized report builders (brand/product/etc.) project from this bundle.
/// </summary>
public sealed record ResearchBundle
{
    public SearchStrategy Strategy { get; init; } = new();
    public IReadOnlyList<SearchResult> WebResults { get; init; } = Array.Empty<SearchResult>();
    public IReadOnlyList<SearchResult> ShoppingResults { get; init; } = Array.Empty<SearchResult>();
    public IReadOnlyList<StoreLocation> Stores { get; init; } = Array.Empty<StoreLocation>();
    public IReadOnlyList<SocialPost> SocialPosts { get; init; } = Array.Empty<SocialPost>();
    public IReadOnlyList<PricePoint> Prices { get; init; } = Array.Empty<PricePoint>();
    public IReadOnlyList<ScrapedPage> Pages { get; init; } = Array.Empty<ScrapedPage>();

    /// <summary>Distinct source URLs the bundle drew on.</summary>
    public IReadOnlyList<string> Sources { get; init; } = Array.Empty<string>();
}

/// <summary>The answer to a free-form <c>ask</c> query.</summary>
public sealed record AgentAnswer
{
    public string Question { get; init; } = string.Empty;
    public string Geo { get; init; } = string.Empty;
    public QueryType QueryType { get; init; }
    public string Summary { get; init; } = string.Empty;
    public ResearchBundle Research { get; init; } = new();

    /// <summary>
    /// Structured product listings, present only when the query was classified as a product
    /// search (e.g. "ACs in Jordan"). Drives the product-grid UI.
    /// </summary>
    public ProductSearchResult? Products { get; init; }

    public DateTimeOffset GeneratedAt { get; init; }
}
