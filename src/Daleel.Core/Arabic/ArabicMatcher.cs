using Daleel.Core.Models;
using Daleel.Core.Pipeline;

namespace Daleel.Core.Arabic;

/// <summary>
/// Matches keywords against post text using <see cref="ArabicNormalizer"/> so that
/// orthographic variants (diacritics, alef/hamza/taa-marbuta forms, tatweel) all
/// collapse before comparison.
/// </summary>
/// <remarks>
/// The matcher supports three modes (see <see cref="MatchMode"/>) and multi-keyword
/// input where <em>any</em> keyword hit flags the post. For fuzzy matching it uses a
/// token-level Levenshtein distance normalized by keyword length, so a one-character
/// typo in a long word still matches while short words stay strict.
/// </remarks>
public class ArabicMatcher : IPostMatcher
{
    public MatchResult Match(
        string text,
        IReadOnlyList<string> keywords,
        MatchMode mode = MatchMode.Contains,
        double fuzzyThreshold = 0.25)
    {
        if (string.IsNullOrEmpty(text) || keywords is null || keywords.Count == 0)
        {
            return MatchResult.NoMatch;
        }

        var normalizedText = ArabicNormalizer.Normalize(text);
        if (normalizedText.Length == 0)
        {
            return MatchResult.NoMatch;
        }

        // Tokenized once and reused across keywords for the fuzzy path.
        string[]? textTokens = null;

        MatchResult? best = null;

        foreach (var rawKeyword in keywords)
        {
            var keyword = ArabicNormalizer.Normalize(rawKeyword);
            if (keyword.Length == 0)
            {
                continue;
            }

            var candidate = mode switch
            {
                MatchMode.Exact => MatchExact(normalizedText, keyword, rawKeyword),
                MatchMode.Contains => MatchContains(text, normalizedText, keyword, rawKeyword),
                MatchMode.Fuzzy => MatchFuzzy(
                    text,
                    normalizedText,
                    textTokens ??= Tokenize(normalizedText),
                    keyword,
                    rawKeyword,
                    fuzzyThreshold),
                _ => MatchResult.NoMatch
            };

            if (candidate.IsMatch && (best is null || candidate.Score > best.Score))
            {
                best = candidate;

                // Exact/contains hits are already maximal; no point scanning further
                // unless we're fuzzy-matching and chasing a higher score.
                if (mode != MatchMode.Fuzzy && candidate.Score >= 1.0)
                {
                    return candidate;
                }
            }
        }

        return best ?? MatchResult.NoMatch;
    }

    private static MatchResult MatchExact(string normalizedText, string keyword, string rawKeyword) =>
        normalizedText == keyword
            ? new MatchResult
            {
                IsMatch = true,
                Score = 1.0,
                MatchedKeyword = rawKeyword,
                Mode = MatchMode.Exact,
                Context = normalizedText
            }
            : MatchResult.NoMatch;

    private static MatchResult MatchContains(
        string originalText, string normalizedText, string keyword, string rawKeyword)
    {
        var index = normalizedText.IndexOf(keyword, StringComparison.Ordinal);
        if (index < 0)
        {
            return MatchResult.NoMatch;
        }

        return new MatchResult
        {
            IsMatch = true,
            Score = 1.0,
            MatchedKeyword = rawKeyword,
            Mode = MatchMode.Contains,
            Context = BuildContext(normalizedText, index, keyword.Length, originalText)
        };
    }

    private static MatchResult MatchFuzzy(
        string originalText,
        string normalizedText,
        string[] textTokens,
        string keyword,
        string rawKeyword,
        double threshold)
    {
        // A contains hit is the strongest possible fuzzy outcome — short-circuit it.
        var containsIndex = normalizedText.IndexOf(keyword, StringComparison.Ordinal);
        if (containsIndex >= 0)
        {
            return new MatchResult
            {
                IsMatch = true,
                Score = 1.0,
                MatchedKeyword = rawKeyword,
                Mode = MatchMode.Fuzzy,
                Context = BuildContext(normalizedText, containsIndex, keyword.Length, originalText)
            };
        }

        double bestScore = 0.0;
        string? bestToken = null;

        foreach (var token in textTokens)
        {
            if (token.Length == 0)
            {
                continue;
            }

            var denom = Math.Max(token.Length, keyword.Length);

            // Edit distance is always >= the length difference, so if the lengths
            // alone push us past the allowed max distance there is no point computing
            // the full matrix — prune before the O(n*m) work.
            var maxDist = threshold * denom;
            if (Math.Abs(token.Length - keyword.Length) > maxDist)
            {
                continue;
            }

            var distance = Levenshtein(token, keyword);
            var similarity = denom == 0 ? 0.0 : 1.0 - (double)distance / denom;

            // Convert the caller's "max distance fraction" threshold into a minimum
            // similarity, then keep the best-scoring token.
            if (1.0 - similarity <= threshold && similarity > bestScore)
            {
                bestScore = similarity;
                bestToken = token;
            }
        }

        if (bestToken is null)
        {
            return MatchResult.NoMatch;
        }

        var tokenIndex = normalizedText.IndexOf(bestToken, StringComparison.Ordinal);
        return new MatchResult
        {
            IsMatch = true,
            Score = bestScore,
            MatchedKeyword = rawKeyword,
            Mode = MatchMode.Fuzzy,
            Context = tokenIndex >= 0
                ? BuildContext(normalizedText, tokenIndex, bestToken.Length, originalText)
                : normalizedText
        };
    }

    private static string[] Tokenize(string normalizedText) =>
        normalizedText.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Builds a readable snippet of normalized text around a match position. We base
    /// the window on the normalized text (where indices are valid) so it stays simple
    /// and predictable; the original text is accepted for future enrichment.
    /// </summary>
    private static string BuildContext(string normalizedText, int matchIndex, int matchLength, string originalText)
    {
        const int window = 20;
        var start = Math.Max(0, matchIndex - window);
        var end = Math.Min(normalizedText.Length, matchIndex + matchLength + window);

        var snippet = normalizedText[start..end];
        var prefix = start > 0 ? "…" : string.Empty;
        var suffix = end < normalizedText.Length ? "…" : string.Empty;

        return $"{prefix}{snippet}{suffix}";
    }

    /// <summary>
    /// Largest <c>b.Length</c> for which the rolling rows are stack-allocated. Beyond
    /// this we fall back to heap arrays so a pathologically long token cannot overflow
    /// the stack. Two rows of (cap + 1) ints ≈ 2 KB at the cap.
    /// </summary>
    private const int StackallocThreshold = 256;

    /// <summary>
    /// Classic Wagner–Fischer Levenshtein edit distance using two rolling rows
    /// (O(n) memory). Operates on already-normalized strings. The rolling rows are
    /// stack-allocated for typical token lengths to avoid two array allocations per
    /// call (this runs once per keyword × text-token pair), with a heap fallback for
    /// very long inputs.
    /// </summary>
    internal static int Levenshtein(string a, string b)
    {
        if (a == b)
        {
            return 0;
        }

        if (a.Length == 0)
        {
            return b.Length;
        }

        if (b.Length == 0)
        {
            return a.Length;
        }

        var width = b.Length + 1;
        int[]? previousRented = null;
        int[]? currentRented = null;

        Span<int> previous = width <= StackallocThreshold
            ? stackalloc int[width]
            : (previousRented = new int[width]);
        Span<int> current = width <= StackallocThreshold
            ? stackalloc int[width]
            : (currentRented = new int[width]);

        for (var j = 0; j < width; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }

            var tmp = previous;
            previous = current;
            current = tmp;
        }

        // previousRented / currentRented are held only to keep the heap fallback
        // buffers rooted for the lifetime of the spans above.
        return previous[b.Length];
    }
}
