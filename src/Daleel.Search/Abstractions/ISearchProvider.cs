using Daleel.Core.Models;

namespace Daleel.Search.Abstractions;

/// <summary>The category of search a provider can serve.</summary>
public enum SearchKind
{
    Web,
    Shopping,
    Maps,
    News
}

/// <summary>
/// A geo- and language-targeted search request. Providers translate this into their own
/// API parameters (e.g. SerpAPI's <c>gl</c>/<c>hl</c>, Bing's <c>mkt</c>).
/// </summary>
public record SearchQuery
{
    public required string Query { get; init; }
    public SearchKind Kind { get; init; } = SearchKind.Web;

    /// <summary>ISO 3166-1 alpha-2 country code for geo-targeting (e.g. "jo").</summary>
    public string? CountryCode { get; init; }

    /// <summary>BCP-47 UI/result language (e.g. "ar").</summary>
    public string? LanguageCode { get; init; }

    /// <summary>Free-text location hint (e.g. "Amman, Jordan").</summary>
    public string? Location { get; init; }

    public int MaxResults { get; init; } = 10;
}

/// <summary>A single search hit, normalized across providers.</summary>
public record SearchResult
{
    public string Title { get; init; } = string.Empty;
    public string? Url { get; init; }
    public string Snippet { get; init; } = string.Empty;

    /// <summary>Which provider produced this result.</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>The result category this hit came from.</summary>
    public SearchKind Kind { get; init; } = SearchKind.Web;

    /// <summary>Price, for shopping results.</summary>
    public Money? Price { get; init; }

    /// <summary>Seller/store, for shopping/maps results.</summary>
    public string? Seller { get; init; }

    /// <summary>Rating 1–5, for shopping/maps results.</summary>
    public double? Rating { get; init; }

    /// <summary>Raw provider position/rank, when known.</summary>
    public int? Position { get; init; }
}

/// <summary>The outcome of one provider call.</summary>
public record SearchResults
{
    public string Provider { get; init; } = string.Empty;
    public string Query { get; init; } = string.Empty;
    public SearchKind Kind { get; init; } = SearchKind.Web;
    public IReadOnlyList<SearchResult> Results { get; init; } = Array.Empty<SearchResult>();

    public static SearchResults Empty(string provider, string query, SearchKind kind) =>
        new() { Provider = provider, Query = query, Kind = kind };
}

/// <summary>A search engine / API that returns ranked results for a query.</summary>
public interface ISearchProvider
{
    string Name { get; }

    /// <summary>Whether this provider can serve the given search kind.</summary>
    bool Supports(SearchKind kind);

    Task<SearchResults> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default);
}
