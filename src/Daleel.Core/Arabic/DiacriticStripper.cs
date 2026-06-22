using System.Globalization;
using System.Text;

namespace Daleel.Core.Arabic;

/// <summary>
/// Removes Arabic diacritics (tashkeel / harakat) and other combining marks
/// from text while leaving the base letters intact.
/// </summary>
/// <remarks>
/// Arabic diacritics are <em>combining</em> marks: they occupy no horizontal space
/// of their own and attach to a preceding base letter. Two visually identical words
/// such as "شَرِكَة" (with fatha/kasra) and "شركة" (bare) carry different code points,
/// so a naive ordinal comparison treats them as different strings. Stripping these
/// marks is therefore the first step toward reliable Arabic keyword matching.
/// </remarks>
public static class DiacriticStripper
{
    // The contiguous Arabic harakat block: U+064B (fathatan) through U+0652 (sukun),
    // plus the less common marks up to U+065F, and the superscript alef U+0670.
    // We also strip U+0610–U+061A (Quranic annotation signs) which are NonSpacingMark.
    private const char ArabicMarkRangeStart = 'ً';
    private const char ArabicMarkRangeEnd = 'ْ';

    /// <summary>
    /// Returns <paramref name="input"/> with all Arabic combining diacritics removed.
    /// </summary>
    public static string Strip(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input ?? string.Empty;
        }

        var sb = new StringBuilder(input.Length);
        foreach (var ch in input)
        {
            if (IsArabicDiacritic(ch))
            {
                continue;
            }

            sb.Append(ch);
        }

        return sb.ToString();
    }

    /// <summary>
    /// True when <paramref name="ch"/> is an Arabic diacritic / combining mark that
    /// should be removed during normalization.
    /// </summary>
    public static bool IsArabicDiacritic(char ch)
    {
        // Fast path: the main harakat block.
        if (ch >= ArabicMarkRangeStart && ch <= ArabicMarkRangeEnd)
        {
            return true;
        }

        // Tashkeel extensions and Quranic annotation marks (U+0653–U+065F).
        if (ch >= 'ٓ' && ch <= 'ٟ')
        {
            return true;
        }

        // Quranic annotation signs that appear above/below letters (U+0610–U+061A).
        if (ch >= 'ؐ' && ch <= 'ؚ')
        {
            return true;
        }

        // Superscript alef (dagger alef).
        if (ch == 'ٰ')
        {
            return true;
        }

        // Catch-all: any remaining Arabic-script combining mark. This is
        // category-aware so we don't have to hardcode every code point.
        if (ch >= '؀' && ch <= 'ۿ' &&
            CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
        {
            return true;
        }

        return false;
    }
}
