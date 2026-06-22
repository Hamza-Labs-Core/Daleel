using Daleel.Core.Models;
using Daleel.Search.Abstractions;

namespace Daleel.Agent;

/// <summary>Tunables and ambient services for an <see cref="AgentService"/>.</summary>
public sealed class AgentOptions
{
    /// <summary>Default market when a query doesn't specify one.</summary>
    public string DefaultGeo { get; init; } = "usa";

    /// <summary>Max results to request per search query.</summary>
    public int ResultsPerQuery { get; init; } = 10;

    /// <summary>Max web/shopping queries to actually execute from a plan (cost guard).</summary>
    public int MaxQueriesPerKind { get; init; } = 4;

    /// <summary>Max URLs to deep-read per run.</summary>
    public int MaxUrlsToRead { get; init; } = 3;

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
    public DateTimeOffset GeneratedAt { get; init; }
}
