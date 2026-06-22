using System.Text;

namespace Daleel.Core.Arabic;

/// <summary>
/// Normalizes Arabic text into a canonical, comparison-friendly form so that
/// orthographic variants of the same word collapse to a single representation.
/// </summary>
/// <remarks>
/// The pipeline applies, in order:
/// <list type="number">
///   <item>Unicode NFC normalization (canonical composition).</item>
///   <item>Diacritic / tashkeel removal via <see cref="DiacriticStripper"/>.</item>
///   <item>Alef-variant folding (أ إ آ ٱ → ا).</item>
///   <item>Alef-maksura → yaa (ى → ي).</item>
///   <item>Taa-marbuta → haa (ة → ه).</item>
///   <item>Tatweel (kashida ـ) removal.</item>
///   <item>Hamza-carrier folding (ؤ → و, ئ → ي, standalone ء dropped).</item>
///   <item>Whitespace collapsing and trimming.</item>
/// </list>
/// Ordering matters: diacritics are stripped <em>before</em> letter folding so a
/// fatha sitting on an alef-with-hamza does not block the alef fold.
/// </remarks>
public static class ArabicNormalizer
{
    /// <summary>
    /// Produces the canonical normalized form of <paramref name="input"/>.
    /// Returns <see cref="string.Empty"/> for null/empty input.
    /// </summary>
    public static string Normalize(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        // 1. Canonical composition first so combining sequences are predictable.
        var text = input.Normalize(NormalizationForm.FormC);

        // 2. Drop tashkeel / harakat.
        text = DiacriticStripper.Strip(text);

        // 3-7. Letter-level folding.
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            sb.Append(FoldChar(ch));
        }

        // 8. Whitespace normalization.
        return CollapseWhitespace(sb.ToString());
    }

    /// <summary>
    /// Maps a single character to its folded canonical form. Characters with no
    /// mapping are returned unchanged. The standalone hamza (ء) folds to the empty
    /// string, so callers must treat the return value as a (possibly empty) span.
    /// </summary>
    private static string FoldChar(char ch) => ch switch
    {
        // Alef variants → bare alef.
        'أ' or 'إ' or 'آ' or 'ٱ' or 'ٲ' or 'ٳ' => "ا",

        // Alef maksura → yaa.
        'ى' => "ي",

        // Taa marbuta → haa.
        'ة' => "ه",

        // Hamza on waw / yaa → bare carrier.
        'ؤ' => "و",
        'ئ' => "ي",

        // Standalone hamza is dropped entirely.
        'ء' => string.Empty,

        // Tatweel (kashida) is decorative stretching → drop.
        'ـ' => string.Empty,

        // Arabic-Indic digits → ASCII digits for consistent matching.
        '٠' => "0",
        '١' => "1",
        '٢' => "2",
        '٣' => "3",
        '٤' => "4",
        '٥' => "5",
        '٦' => "6",
        '٧' => "7",
        '٨' => "8",
        '٩' => "9",

        _ => ch.ToString()
    };

    /// <summary>
    /// Collapses any run of whitespace into a single ASCII space and trims the ends.
    /// </summary>
    private static string CollapseWhitespace(string text)
    {
        var sb = new StringBuilder(text.Length);
        var inWhitespace = false;

        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                inWhitespace = true;
                continue;
            }

            if (inWhitespace && sb.Length > 0)
            {
                sb.Append(' ');
            }

            inWhitespace = false;
            sb.Append(ch);
        }

        return sb.ToString();
    }
}
