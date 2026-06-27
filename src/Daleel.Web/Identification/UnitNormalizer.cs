using System.Globalization;
using System.Text.RegularExpressions;

namespace Daleel.Web.Identification;

/// <summary>
/// Canonicalizes spec values whose units differ across sources so the same measurement reads identically
/// regardless of where it was scraped. The headline case is screen/display size, which Jordanian listings
/// quote as <c>55 inches</c>, <c>55"</c>, or <c>139.7cm</c> — this folds them all to the single canonical
/// form <c>55 inches / 139.7 cm</c> (both units, so a shopper sees what they expect either way).
/// </summary>
public static partial class UnitNormalizer
{
    private const double CmPerInch = 2.54;

    // A leading magnitude followed by a length unit. Quotes (" '') and in/inch(es) are inches; cm/mm metric.
    // A negative-lookahead end (?![a-z]) rather than \b: the quote units (" '') are non-word characters,
    // after which \b would never match — so 55" would be missed.
    [GeneratedRegex(@"^\s*(?<num>\d+(?:[.,]\d+)?)\s*(?<unit>inches|inch|in|""|''|cm|centimetres|centimeters|centimetre|centimeter|mm)(?![a-z])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LengthPattern();

    // Collapses runs of whitespace so "55   inch" and "55 inch" don't look like different values.
    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    /// <summary>
    /// Returns the canonical form of <paramref name="value"/>. Length measurements (inches/cm/mm) become
    /// <c>"{inches} inches / {cm} cm"</c>; everything else is whitespace-collapsed and trimmed unchanged.
    /// </summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var collapsed = Whitespace().Replace(value.Trim(), " ");

        var m = LengthPattern().Match(collapsed);
        if (!m.Success)
        {
            return collapsed;
        }

        if (!double.TryParse(m.Groups["num"].Value.Replace(',', '.'),
                NumberStyles.Float, CultureInvariant, out var magnitude))
        {
            return collapsed;
        }

        var unit = m.Groups["unit"].Value.ToLowerInvariant();
        double inches = unit switch
        {
            "cm" or "centimetre" or "centimeter" or "centimetres" or "centimeters" => magnitude / CmPerInch,
            "mm" => magnitude / 10.0 / CmPerInch,
            _ => magnitude, // inches family: inch, inches, in, ", ''
        };

        var cm = inches * CmPerInch;
        return $"{Trim(inches)} inches / {Trim(cm)} cm";
    }

    private static CultureInfo CultureInvariant => CultureInfo.InvariantCulture;

    /// <summary>Formats a magnitude with up to one decimal, dropping a trailing ".0".</summary>
    private static string Trim(double v)
    {
        var rounded = Math.Round(v, 1, MidpointRounding.AwayFromZero);
        return rounded == Math.Truncate(rounded)
            ? ((long)rounded).ToString(CultureInvariant)
            : rounded.ToString("0.0", CultureInvariant);
    }
}
