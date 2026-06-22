using System.Globalization;

namespace Daleel.Core.Models;

/// <summary>
/// A monetary amount in a specific currency. Kept deliberately simple (decimal + ISO
/// code) — Daleel compares and ranks prices but does not do currency conversion, so a
/// <see cref="PricePoint"/> always records the price in the currency it was found in.
/// </summary>
public readonly record struct Money(decimal Amount, string Currency)
{
    public override string ToString() =>
        $"{Amount.ToString("0.##", CultureInfo.InvariantCulture)} {Currency}";

    /// <summary>Formats with a symbol when one is known, else the ISO code.</summary>
    public string ToDisplay() => Currency switch
    {
        "USD" => $"${Amount.ToString("0.##", CultureInfo.InvariantCulture)}",
        "JOD" => $"{Amount.ToString("0.###", CultureInfo.InvariantCulture)} JD",
        "SAR" => $"{Amount.ToString("0.##", CultureInfo.InvariantCulture)} SAR",
        "AED" => $"{Amount.ToString("0.##", CultureInfo.InvariantCulture)} AED",
        "EGP" => $"{Amount.ToString("0.##", CultureInfo.InvariantCulture)} EGP",
        _ => ToString()
    };
}
