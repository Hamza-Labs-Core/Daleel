namespace Daleel.Core.Models;

/// <summary>
/// Approximate currency conversion for <em>display only</em>, so a price quoted in another
/// currency can be shown next to the market's currency (e.g. "510 AED ≈ 99 JOD").
/// </summary>
/// <remarks>
/// Daleel never converts for ranking or comparison — offers are compared within a single
/// currency. These are static, rounded indicative rates (pegged Gulf currencies are stable;
/// others drift), so converted values are always shown with a "≈" and should be treated as
/// ballpark. This is reference data, not a per-country source list.
/// </remarks>
public static class CurrencyConverter
{
    // Indicative value of one unit of each currency expressed in JOD (Jordanian dinar) as the hub.
    private static readonly IReadOnlyDictionary<string, decimal> JodPerUnit =
        new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
        {
            ["JOD"] = 1m,
            ["USD"] = 0.709m,
            ["SAR"] = 0.189m,
            ["AED"] = 0.193m,
            ["EGP"] = 0.0143m,
            ["EUR"] = 0.77m,
            ["GBP"] = 0.90m,
        };

    /// <summary>True when both currencies have an indicative rate and differ.</summary>
    public static bool CanConvert(string? from, string? to) =>
        !string.IsNullOrWhiteSpace(from) && !string.IsNullOrWhiteSpace(to) &&
        !from!.Equals(to, StringComparison.OrdinalIgnoreCase) &&
        JodPerUnit.ContainsKey(from) && JodPerUnit.ContainsKey(to!);

    /// <summary>
    /// Converts <paramref name="amount"/> from one currency to another using indicative rates.
    /// Returns null when either currency is unknown (caller then shows the original only).
    /// </summary>
    public static decimal? Convert(decimal amount, string? from, string? to)
    {
        if (!CanConvert(from, to))
        {
            return null;
        }

        var inJod = amount * JodPerUnit[from!];
        var converted = inJod / JodPerUnit[to!];
        return Math.Round(converted, 2);
    }

    /// <summary>Converts a <see cref="Money"/> into the target currency as a new (approximate) <see cref="Money"/>.</summary>
    public static Money? Convert(Money money, string toCurrency) =>
        Convert(money.Amount, money.Currency, toCurrency) is { } a ? new Money(a, toCurrency) : null;
}
