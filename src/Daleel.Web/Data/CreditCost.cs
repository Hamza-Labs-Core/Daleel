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
    public const int WorkersAiCall = 1;     // one Workers-AI inference batch (classify/extract/filter)
    public const int EdgeDrain = 1;         // landing one drained edge result (queue ops + R2 read)

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
        // Edge execution bills at fixed weights — routing work to the workers must never change a
        // user's bill via the dollar fall-through. "cloudflare/" (the drain) is prefix-matched so the
        // browser-render provider ("cloudflare-browser") keeps its existing pricing.
        if (p.StartsWith("workers-ai", StringComparison.Ordinal))
        {
            return WorkersAiCall;
        }
        if (p.StartsWith("cloudflare/", StringComparison.Ordinal))
        {
            return EdgeDrain;
        }
        if (p.Contains("serp"))
        {
            return SerpApiPage;
        }
        // "place" also matches the edge-proxied "search-worker/google-places" — a proxied Places
        // call must cost the user exactly what a direct one does.
        if (p.Contains("place") || p.Contains("maps"))
        {
            return GooglePlaces;
        }
        // "scrape-worker" covers edge-submitted crawls/page fetches — they bill as their inline
        // Context.dev equivalents, whatever vendor the worker fronts.
        if (p.Contains("context") || p.Contains("scrape-worker"))
        {
            // "catalog" covers the gateway's canonical "catalog/extract" endpoint — a full catalogue
            // crawl must bill at the catalogue rate, not as a single page scrape.
            return e.Contains("product") || e.Contains("brand/ai") || e.Contains("catalog")
                ? ContextCatalogue
                : ContextScrape;
        }

        return (int)Math.Ceiling(Math.Max(0m, estimatedCost) * 1000m);
    }
}
