using Daleel.Core.Models;

namespace Daleel.Agent;

/// <summary>
/// Deterministic backstop for the planner's query-type classification. The LLM planner occasionally
/// classifies an obvious buy-intent query ("best espresso machine") as <see cref="QueryType.General"/>,
/// which silently skips the ENTIRE product pipeline — no extraction, no grid, and the user sees
/// "No results just yet" (QA job 4, 2026-07-02). A shopping assistant must never coin-flip on that,
/// so a query that is unmistakably buy-intent-shaped is coerced to
/// <see cref="QueryType.ProductResearch"/> when the LLM said General.
/// </summary>
/// <remarks>
/// Deliberately conservative: it only ever UPGRADES General → ProductResearch (a specific LLM
/// classification like BrandLookup or OpinionAggregation is never overridden), and "best/top …"
/// phrasing counts only when it isn't advice-shaped ("best way to …", "how to …") — a wrong upgrade
/// would run product extraction on an advice question and show an empty grid instead of the narrative.
/// </remarks>
public static class BuyIntentHeuristic
{
    /// <summary>Upgrades General to ProductResearch when the query is unmistakably buy-intent-shaped.</summary>
    public static QueryType Coerce(QueryType classified, string? query) =>
        classified == QueryType.General && LooksLikeBuyIntent(query)
            ? QueryType.ProductResearch
            : classified;

    /// <summary>True when the query reads as "help me choose/buy a product" in English or Arabic.</summary>
    public static bool LooksLikeBuyIntent(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return false;
        }

        var q = " " + query.Trim().ToLowerInvariant() + " ";

        // Advice-shaped phrasing wins: "best way to learn arabic" / "how to descale a coffee maker"
        // are questions, not shopping trips — never coerce those.
        foreach (var marker in AdviceMarkers)
        {
            if (q.Contains(marker, StringComparison.Ordinal))
            {
                return false;
            }
        }

        // Explicit purchase vocabulary is buy intent wherever it appears.
        foreach (var marker in PurchaseMarkers)
        {
            if (q.Contains(marker, StringComparison.Ordinal))
            {
                return true;
            }
        }

        // "best/top/أفضل <short thing>" — the canonical product-research phrasing. Kept to short
        // queries: a long "best ..." sentence is far more likely a nuanced question for the analyst.
        var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length is >= 2 and <= 6)
        {
            foreach (var prefix in RankingPrefixes)
            {
                if (words[0].Equals(prefix, StringComparison.Ordinal) ||
                    (words[0] == "the" && words.Length >= 3 && words[1].Equals(prefix, StringComparison.Ordinal)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    // Substring markers are padded/spaced by the caller's normalization; Arabic has no case to fold.
    private static readonly string[] AdviceMarkers =
    {
        " way to ", " ways to ", " how to ", " how do ", " how can ", " time to ", " tips ",
        " method ", " guide to ", " learn ", " طريقة ", " كيف ", " كيفية ", " تعلم ",
    };

    private static readonly string[] PurchaseMarkers =
    {
        " buy ", " buying ", " purchase ", " cheapest ", " price ", " prices ", " deal ", " deals ",
        " discount ", " offer ", " offers ", " under $", " for sale ", " where to get ",
        " شراء ", " اشتري ", " سعر ", " اسعار ", " أسعار ", " ارخص ", " أرخص ", " عروض ", " خصم ", " تخفيضات ", " للبيع ",
    };

    private static readonly string[] RankingPrefixes =
    {
        "best", "top", "أفضل", "افضل", "احسن", "أحسن",
    };
}
