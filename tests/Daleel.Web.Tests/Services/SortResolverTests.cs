using Daleel.Core.Models;
using Daleel.Web.Services;
using FluentAssertions;
using Xunit;

namespace Daleel.Web.Tests.Services;

/// <summary>
/// The goal→sort resolution chain: explicit DefaultSort wins when it's a known key; otherwise a
/// keyword heuristic over the free-text Goal; otherwise "relevance". Unknown/junk never leaks
/// through to the grid's sort switch.
/// </summary>
public class SortResolverTests
{
    [Theory]
    [InlineData("price_asc", "whatever", "price_asc")]   // explicit known key wins
    [InlineData("rating", "", "rating")]
    [InlineData("PRICE_DESC", "", "price_desc")]         // case-insensitive
    public void ExplicitKnownDefaultSort_Wins(string defaultSort, string goal, string expected) =>
        SortResolver.Resolve(new SearchStrategy { DefaultSort = defaultSort, Goal = goal })
            .Should().Be(expected);

    [Theory]
    [InlineData("cheapest", "price_asc")]
    [InlineData("lowest price", "price_asc")]
    [InlineData("best", "rating")]
    [InlineData("best for newborns", "rating")]
    [InlineData("top rated", "rating")]
    [InlineData("highest quality", "rating")]
    [InlineData("most expensive", "price_desc")]
    [InlineData("premium", "price_desc")]
    [InlineData("أرخص", "price_asc")]                  // Arabic: cheapest
    [InlineData("أفضل", "rating")]                     // Arabic: best
    [InlineData("أغلى", "price_desc")]                 // Arabic: most expensive
    [InlineData("best cheap option", "price_asc")]     // price keywords outrank quality keywords
    public void GoalKeywordHeuristic_WhenDefaultSortMissing(string goal, string expected) =>
        SortResolver.Resolve(new SearchStrategy { Goal = goal }).Should().Be(expected);

    [Theory]
    [InlineData("bogus_sort", "banana")] // unknown key + unmatchable goal
    [InlineData("", "")]
    public void FallsBackToRelevance(string defaultSort, string goal) =>
        SortResolver.Resolve(new SearchStrategy { DefaultSort = defaultSort, Goal = goal })
            .Should().Be("relevance");

    [Fact]
    public void NullStrategy_IsRelevance() => SortResolver.Resolve(null).Should().Be("relevance");
}
