using Daleel.Core.Models;

namespace Daleel.Web.Services;

/// <summary>
/// Resolves the grid's initial sort from the search object. Chain: the planner's explicit
/// <see cref="SearchStrategy.DefaultSort"/> when it names a KNOWN sort key → a keyword heuristic
/// over the free-text <see cref="SearchStrategy.Goal"/> → "relevance". Pure and total: any input,
/// including null, yields a key the grid's sort switch understands.
/// </summary>
public static class SortResolver
{
    /// <summary>Every sort key the grid understands (must match ProductListings' sort switch).</summary>
    private static readonly HashSet<string> KnownSorts = new(StringComparer.OrdinalIgnoreCase)
    {
        "relevance", "price_asc", "price_desc", "rating", "sellers"
    };

    public static string Resolve(SearchStrategy? strategy)
    {
        if (strategy is null)
        {
            return "relevance";
        }

        if (KnownSorts.Contains(strategy.DefaultSort))
        {
            return strategy.DefaultSort.ToLowerInvariant();
        }

        var goal = strategy.Goal.ToLowerInvariant();
        if (goal.Length == 0)
        {
            return "relevance";
        }

        // Order matters: "best price" should hit the price rule, so price keywords are checked
        // before the quality words that ride along in phrases like "best cheap option".
        if (ContainsAny(goal, "cheap", "lowest", "أرخص", "affordable", "budget"))
        {
            return "price_asc";
        }
        if (ContainsAny(goal, "expensive", "premium", "luxury", "high end", "high-end", "أغلى"))
        {
            return "price_desc";
        }
        if (ContainsAny(goal, "best", "top", "rated", "quality", "reliable", "أفضل"))
        {
            return "rating";
        }

        return "relevance";
    }

    private static bool ContainsAny(string goal, params string[] words) =>
        words.Any(w => goal.Contains(w, StringComparison.Ordinal));
}
