using Daleel.Core.Models;

namespace Daleel.Core.Pipeline;

/// <summary>
/// Matches a set of keywords against a post's text and reports the outcome.
/// </summary>
public interface IPostMatcher
{
    /// <summary>
    /// Matches <paramref name="keywords"/> against <paramref name="text"/> using
    /// <paramref name="mode"/>. Returns the best match found, or
    /// <see cref="MatchResult.NoMatch"/> when nothing matches.
    /// </summary>
    MatchResult Match(
        string text,
        IReadOnlyList<string> keywords,
        MatchMode mode = MatchMode.Contains,
        double fuzzyThreshold = 0.25);
}
