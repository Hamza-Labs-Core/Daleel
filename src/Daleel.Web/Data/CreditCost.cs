namespace Daleel.Web.Data;

/// <summary>
/// Translates a single external API call into billable credits. Per-provider prices keep a search's
/// cost legible to users ("a deep search ≈ 40 credits") while reflecting that each search is different
/// — a quick cache hit costs nothing, a deep multi-page crawl costs more. These defaults match the
/// pricing-page copy and the FAQ; they live in one place so they're easy to tune.
/// </summary>
public static class CreditCost
{
    public const int SerpApiPage = 5;
    public const int GooglePlaces = 3;
    public const int ContextScrape = 2;
    public const int ContextCatalogue = 10; // /v1/brand/ai/products — a full-site product crawl
    public const int LlmPer1KTokens = 1;

    /// <summary>
    /// Credits for one call. LLM usage bills by tokens (1 credit / 1K, min 1); known providers bill a
    /// flat per-call rate; anything else falls back to its estimated dollar cost (×1000, i.e. 1 credit ≈
    /// $0.001) so a new provider is never silently free.
    /// </summary>
    public static int ForCall(string? provider, string? endpoint, int? inputTokens, int? outputTokens, decimal estimatedCost)
    {
        // Token-driven LLM calls, regardless of which gateway served them.
        if (inputTokens is > 0 || outputTokens is > 0)
        {
            var thousands = ((inputTokens ?? 0) + (outputTokens ?? 0)) / 1000.0;
            return Math.Max(1, (int)Math.Ceiling(thousands * LlmPer1KTokens));
        }

        var p = (provider ?? string.Empty).ToLowerInvariant();
        var e = (endpoint ?? string.Empty).ToLowerInvariant();

        if (p == "cache")
        {
            return 0; // a cache hit made no external call
        }
        if (p.Contains("serp"))
        {
            return SerpApiPage;
        }
        if (p.Contains("place") || p.Contains("maps"))
        {
            return GooglePlaces;
        }
        if (p.Contains("context"))
        {
            return e.Contains("product") || e.Contains("brand/ai") ? ContextCatalogue : ContextScrape;
        }

        return (int)Math.Ceiling(Math.Max(0m, estimatedCost) * 1000m);
    }
}
