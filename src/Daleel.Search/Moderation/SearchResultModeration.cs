using Daleel.Core.Moderation;
using Daleel.Search.Abstractions;

namespace Daleel.Search.Moderation;

/// <summary>
/// Bridges the Core moderation pipeline to <see cref="SearchResult"/> (in this assembly).
/// Core can't reference Search without a cycle, so the typed projection and the legacy
/// keyword-only extension live here.
/// </summary>
public static class SearchResultModeration
{
    /// <summary>
    /// How a search hit is moderated: named fields (title/snippet/seller) so findings can say
    /// where the match was, its URL for the admin log's source link, and its image — which the
    /// vision pass may strip individually without removing the result.
    /// </summary>
    public static readonly ModerationProjection<SearchResult> Projection = new(
        "SearchResult",
        r => new[] { ("title", (string?)r.Title), ("snippet", (string?)r.Snippet), ("seller", r.Seller) },
        SourceUrl: r => r.Url,
        ImageUrl: r => r.ImageUrl,
        WithImageUrl: (r, url) => r with { ImageUrl = url });

    /// <summary>Removes search hits whose title/snippet/seller mention non-halal content (keyword-only path).</summary>
    public static List<SearchResult> FilterSearchResults(this ContentFilter filter, IEnumerable<SearchResult> results) =>
        filter.FilterResults(results, r => $"{r.Title} {r.Snippet} {r.Seller}");
}
