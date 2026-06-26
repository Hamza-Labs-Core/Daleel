using System.Globalization;
using System.Text.RegularExpressions;

namespace Daleel.Core.Pricing;

/// <summary>One price found in scraped page text, with the line it came from for later token matching.</summary>
public readonly record struct PriceMatch(decimal Price, string Currency, string Line);

/// <summary>
/// Multi-price extraction over scraped page markdown. This is the fallback path used when Context.dev's
/// structured catalogue endpoint can't read a store (JS-heavy/anti-bot sites that only the Cloudflare
/// Browser renderer gets through) — we render the page to markdown and pull candidate prices out of it
/// here. Pure and deterministic so the messy matching rules are unit-tested in isolation. Lives on the
/// same <see cref="PriceParser"/> type as the single-value <c>TryParse</c> so all price parsing has one home.
/// </summary>
public static partial class PriceParser
{
    /// <summary>Symbol/code → ISO-ish currency code. Covers the app's MENA + Western markets.</summary>
    private static readonly (string Token, string Code)[] ExtractCurrencyMap =
    {
        ("JOD", "JOD"), ("JD", "JOD"), ("د.ا", "JOD"),
        ("SAR", "SAR"), ("ر.س", "SAR"),
        ("AED", "AED"), ("د.إ", "AED"),
        ("EGP", "EGP"),
        ("USD", "USD"), ("$", "USD"),
        ("EUR", "EUR"), ("€", "EUR"),
        ("GBP", "GBP"), ("£", "GBP")
    };

    // Currency symbol/code immediately BEFORE the number ($1,299 / JOD 450 / د.ا ٤٥٠).
    [GeneratedRegex(@"(JOD|JD|SAR|AED|EGP|USD|EUR|GBP|\$|€|£|د\.ا|ر\.س|د\.إ)\s?(\d[\d,]*(?:\.\d{1,2})?)",
        RegexOptions.IgnoreCase)]
    private static partial Regex SymbolFirst();

    // Number BEFORE an alphabetic code (450 JOD / 1299 USD). Symbols don't trail, so codes only here.
    [GeneratedRegex(@"(\d[\d,]*(?:\.\d{1,2})?)\s?(JOD|JD|SAR|AED|EGP|USD|EUR|GBP)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex NumberFirst();

    /// <summary>
    /// All prices found in <paramref name="text"/>, scanned line by line so each match keeps the line it
    /// came from (the matcher uses that line's words to decide which product a price belongs to). Capped
    /// at <paramref name="maxResults"/> so a huge catalogue page can't blow up downstream matching.
    /// </summary>
    public static IReadOnlyList<PriceMatch> Extract(string? text, int maxResults = 200)
    {
        var results = new List<PriceMatch>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return results;
        }

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            foreach (Match m in SymbolFirst().Matches(line))
            {
                if (TryAdd(results, m.Groups[1].Value, m.Groups[2].Value, line) && results.Count >= maxResults)
                {
                    return results;
                }
            }

            foreach (Match m in NumberFirst().Matches(line))
            {
                if (TryAdd(results, m.Groups[2].Value, m.Groups[1].Value, line) && results.Count >= maxResults)
                {
                    return results;
                }
            }
        }

        return results;
    }

    private static bool TryAdd(List<PriceMatch> results, string currencyToken, string number, string line)
    {
        var code = NormalizeExtractedCurrency(currencyToken);
        if (code is null || !TryParseExtractedPrice(number, out var price))
        {
            return false;
        }

        results.Add(new PriceMatch(price, code, line));
        return true;
    }

    /// <summary>Maps a symbol/code as it appeared on the page to a canonical currency code, or null.</summary>
    private static string? NormalizeExtractedCurrency(string token)
    {
        var t = token.Trim();
        foreach (var (sym, code) in ExtractCurrencyMap)
        {
            if (string.Equals(t, sym, StringComparison.OrdinalIgnoreCase))
            {
                return code;
            }
        }

        return null;
    }

    private static bool TryParseExtractedPrice(string raw, out decimal price)
    {
        // Strip grouping commas; the regex already constrained the shape to a money number.
        var cleaned = raw.Replace(",", string.Empty);
        return decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out price)
               && price > 0;
    }
}
