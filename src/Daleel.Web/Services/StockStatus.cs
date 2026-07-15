namespace Daleel.Web.Services;

/// <summary>The three stock states the UI can act on, plus Unknown (renders nothing).</summary>
public enum Stock
{
    Unknown,
    InStock,
    OutOfStock,
    Preorder
}

/// <summary>
/// Folds a store's free-form availability wording ("In Stock", "متوفر", "sold out", "pre-order" —
/// LLM-extracted, so every store's own phrasing) into the <see cref="Stock"/> states the card chip
/// shows. Negative phrasings are checked FIRST because "out of stock" contains "stock". Pure and
/// total: any input, including null, yields a state.
/// </summary>
public static class StockStatus
{
    public static Stock Classify(string? availability)
    {
        if (string.IsNullOrWhiteSpace(availability))
        {
            return Stock.Unknown;
        }

        var a = availability.Trim().ToLowerInvariant();

        // Negatives first — they often CONTAIN the positive words ("out of stock", "غير متوفر").
        if (ContainsAny(a, "out of stock", "outofstock", "out-of-stock", "sold out", "soldout",
                "unavailable", "غير متوفر", "نفذ", "غير متاح"))
        {
            return Stock.OutOfStock;
        }

        if (ContainsAny(a, "preorder", "pre-order", "pre order", "الطلب المسبق", "طلب مسبق"))
        {
            return Stock.Preorder;
        }

        if (ContainsAny(a, "in stock", "instock", "in-stock", "available", "متوفر", "متاح"))
        {
            return Stock.InStock;
        }

        return Stock.Unknown;
    }

    private static bool ContainsAny(string value, params string[] words) =>
        words.Any(w => value.Contains(w, StringComparison.Ordinal));
}
