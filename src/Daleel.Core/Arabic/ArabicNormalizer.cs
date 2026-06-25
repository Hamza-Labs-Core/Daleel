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
            FoldChar(ch, sb);
        }

        // 8. Whitespace normalization.
        return CollapseWhitespace(sb.ToString());
    }

    /// <summary>
    /// Appends the folded canonical form of a single character to <paramref name="sb"/>.
    /// Characters with no mapping are appended unchanged; the standalone hamza (ء) and
    /// tatweel (ـ) fold to nothing (no append). Writing directly into the builder avoids
    /// allocating a one-character <see cref="string"/> per input character — this is the
    /// hottest path in the system, so the per-char allocation matters.
    /// </summary>
    private static void FoldChar(char ch, StringBuilder sb)
    {
        switch (ch)
        {
            // Alef variants → bare alef.
            case 'أ' or 'إ' or 'آ' or 'ٱ' or 'ٲ' or 'ٳ':
                sb.Append('ا');
                break;

            // Alef maksura → yaa.
            case 'ى':
                sb.Append('ي');
                break;

            // Taa marbuta → haa.
            case 'ة':
                sb.Append('ه');
                break;

            // Hamza on waw / yaa → bare carrier.
            case 'ؤ':
                sb.Append('و');
                break;
            case 'ئ':
                sb.Append('ي');
                break;

            // Standalone hamza and tatweel (kashida) are dropped entirely.
            case 'ء' or 'ـ':
                break;

            // Arabic-Indic digits → ASCII digits for consistent matching.
            case '٠':
                sb.Append('0');
                break;
            case '١':
                sb.Append('1');
                break;
            case '٢':
                sb.Append('2');
                break;
            case '٣':
                sb.Append('3');
                break;
            case '٤':
                sb.Append('4');
                break;
            case '٥':
                sb.Append('5');
                break;
            case '٦':
                sb.Append('6');
                break;
            case '٧':
                sb.Append('7');
                break;
            case '٨':
                sb.Append('8');
                break;
            case '٩':
                sb.Append('9');
                break;

            default:
                sb.Append(ch);
                break;
        }
    }

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
