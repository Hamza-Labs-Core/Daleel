using System.Text.RegularExpressions;
using Daleel.Core.Models;
using Daleel.Core.Pricing;

namespace Daleel.Pipeline;

/// <summary>
/// Extracts and normalizes prices from free-form text and tracks observed price points
/// for a product, exposing simple summary statistics (min / max / median).
/// </summary>
/// <remarks>
/// Extraction delegates per-token parsing to <see cref="PriceParser"/> (which handles
/// currencies, Arabic-Indic digits, and separators) and adds the job of finding the
/// price-bearing fragments inside a larger blob of text — e.g. a scraped listing page or
/// a search snippet that mentions several prices.
/// </remarks>
public sealed partial class PriceTracker
{
    private readonly string _defaultCurrency;
    private readonly List<PricePoint> _points = new();

    public PriceTracker(string defaultCurrency = "USD") => _defaultCurrency = defaultCurrency;

    /// <summary>All price points recorded so far.</summary>
    public IReadOnlyList<PricePoint> Points => _points;

    // Matches a number optionally surrounded by a currency symbol/word, e.g.
    // "$1,299", "450 دينار", "JOD 1299.00", "2500 AED".
    [GeneratedRegex(
        @"(?:[$€£﷼]|USD|JOD|JD|SAR|AED|EGP|EUR|GBP|دينار|ريال|درهم|جنيه)?\s*\d[\d.,]*\s*" +
        @"(?:[$€£﷼]|USD|JOD|JD|SAR|AED|EGP|EUR|GBP|دينار|ريال|درهم|جنيه)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PriceFragmentRegex();

    /// <summary>
    /// Extracts every distinct price found in <paramref name="text"/>. A fragment only
    /// counts if it actually carries a currency cue or a decimal amount, to avoid
    /// matching bare counts like "5 reviews".
    /// </summary>
    public IReadOnlyList<Money> ExtractPrices(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<Money>();
        }

        var found = new List<Money>();
        foreach (Match fragment in PriceFragmentRegex().Matches(text))
        {
            var value = fragment.Value.Trim();
            if (!LooksLikePrice(value))
            {
                continue;
            }

            if (PriceParser.TryParse(value, out var money, _defaultCurrency))
            {
                found.Add(money);
            }
        }

        return found;
    }

    /// <summary>Records a price point for later summarization.</summary>
    public void Track(PricePoint point) => _points.Add(point);

    /// <summary>Extracts prices from text and records them as points for a product.</summary>
    public void TrackFromText(string product, string? text, string? store = null, string? url = null)
    {
        foreach (var price in ExtractPrices(text))
        {
            _points.Add(new PricePoint { Product = product, Price = price, Store = store, Url = url });
        }
    }

    /// <summary>Lowest tracked price within a single currency (the most common one).</summary>
    public Money? Lowest() => Bound(ascending: true);

    /// <summary>Highest tracked price within a single currency.</summary>
    public Money? Highest() => Bound(ascending: false);

    private Money? Bound(bool ascending)
    {
        var group = _points
            .Where(p => p.Price.Amount > 0)
            .GroupBy(p => p.Price.Currency)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault();

        if (group is null)
        {
            return null;
        }

        var ordered = group.OrderBy(p => p.Price.Amount).ToList();
        return (ascending ? ordered.First() : ordered.Last()).Price;
    }

    /// <summary>
    /// True when a fragment carries a real price cue: a currency token, or a decimal
    /// amount. A plain integer with no currency is rejected.
    /// </summary>
    private static bool LooksLikePrice(string fragment)
    {
        var hasCurrency = fragment.IndexOfAny(new[] { '$', '€', '£', '﷼' }) >= 0 ||
                          Regex.IsMatch(fragment, "(?i)usd|jod|jd|sar|aed|egp|eur|gbp|دينار|ريال|درهم|جنيه");
        var hasDecimal = Regex.IsMatch(fragment, @"\d[.,]\d");
        return hasCurrency || hasDecimal;
    }
}
