using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Daleel.Core.Arabic;
using Daleel.Core.Models;

namespace Daleel.Core.Pricing;

/// <summary>
/// Parses free-form price strings (from search snippets, store pages, listings) into a
/// normalized <see cref="Money"/>. Handles multiple currencies, currency symbols, Arabic
/// currency words, Arabic-Indic digits, and thousands/decimal separators.
/// </summary>
public static partial class PriceParser
{
    // Currency detection: symbol or code or Arabic word → ISO code.
    private static readonly (string Token, string Code)[] CurrencyTokens =
    {
        ("$", "USD"), ("usd", "USD"), ("dollar", "USD"),
        ("jod", "JOD"), ("jd", "JOD"), ("دينار", "JOD"), ("دينار اردني", "JOD"),
        ("sar", "SAR"), ("ريال", "SAR"), ("﷼", "SAR"),
        ("aed", "AED"), ("درهم", "AED"), ("dhs", "AED"),
        ("egp", "EGP"), ("جنيه", "EGP"), ("le", "EGP"),
        ("€", "EUR"), ("eur", "EUR"),
        ("£", "GBP"), ("gbp", "GBP"),
    };

    [GeneratedRegex(@"\d[\d.,٬٫\s]*\d|\d", RegexOptions.CultureInvariant)]
    private static partial Regex NumberRegex();

    /// <summary>
    /// Attempts to parse a price out of <paramref name="text"/>. <paramref name="defaultCurrency"/>
    /// is used when no currency token is detected in the text.
    /// </summary>
    public static bool TryParse(string? text, out Money money, string defaultCurrency = "USD")
    {
        money = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        // Fold Arabic-Indic digits to ASCII first (reuses the normalizer's digit map).
        var normalized = ArabicNormalizer.Normalize(text);
        var lower = normalized.ToLowerInvariant();

        var currency = DetectCurrency(lower) ?? defaultCurrency;

        var match = NumberRegex().Match(normalized);
        if (!match.Success)
        {
            return false;
        }

        if (!TryParseAmount(match.Value, out var amount))
        {
            return false;
        }

        money = new Money(amount, currency);
        return true;
    }

    /// <summary>Parses or throws.</summary>
    public static Money Parse(string text, string defaultCurrency = "USD") =>
        TryParse(text, out var money, defaultCurrency)
            ? money
            : throw new FormatException($"Could not parse a price from '{text}'.");

    private static string? DetectCurrency(string lowerText)
    {
        // Prefer the longest matching token so "دينار اردني" beats "دينار", etc.
        string? best = null;
        var bestLen = 0;
        foreach (var (token, code) in CurrencyTokens)
        {
            if (lowerText.Contains(token, StringComparison.Ordinal) && token.Length > bestLen)
            {
                best = code;
                bestLen = token.Length;
            }
        }

        return best;
    }

    /// <summary>
    /// Parses a numeric run, inferring whether ',' or '.' is the decimal separator from
    /// its position. Strips spaces and Arabic separators.
    /// </summary>
    private static bool TryParseAmount(string raw, out decimal amount)
    {
        amount = 0;

        // Remove spaces and Arabic thousands/decimal separators we don't need to keep.
        var cleaned = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (char.IsDigit(ch) || ch == '.' || ch == ',')
            {
                cleaned.Append(ch);
            }
        }

        var s = cleaned.ToString();
        if (s.Length == 0)
        {
            return false;
        }

        var lastComma = s.LastIndexOf(',');
        var lastDot = s.LastIndexOf('.');

        if (lastComma >= 0 && lastDot >= 0)
        {
            // Whichever appears last is the decimal separator; the other groups thousands.
            if (lastComma > lastDot)
            {
                s = s.Replace(".", string.Empty).Replace(',', '.');
            }
            else
            {
                s = s.Replace(",", string.Empty);
            }
        }
        else if (lastComma >= 0)
        {
            // Only commas: treat as decimal if exactly 1–2 trailing digits, else thousands.
            var after = s.Length - lastComma - 1;
            s = after is 1 or 2 ? s.Replace(',', '.') : s.Replace(",", string.Empty);
        }
        // Only dots (or none): standard invariant parse handles it.

        return decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out amount);
    }
}
