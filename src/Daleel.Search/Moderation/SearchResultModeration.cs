using Daleel.Core.Moderation;
using Daleel.Search.Abstractions;

namespace Daleel.Search.Moderation;

/// <summary>
/// Bridges <see cref="ContentFilter"/> (in Daleel.Core) to <see cref="SearchResult"/> (in this
/// assembly). Core can't reference Search without a cycle, so the typed search-result filter lives
/// here as an extension over the generic core filter.
/// </summary>
public static class SearchResultModeration
{
    /// <summary>Removes search hits whose title/snippet/seller mention non-halal content.</summary>
    public static List<SearchResult> FilterSearchResults(this ContentFilter filter, IEnumerable<SearchResult> results) =>
        filter.FilterResults(results, r => $"{r.Title} {r.Snippet} {r.Seller}");
}
