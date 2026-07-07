namespace Daleel.Core.Observability;

/// <summary>Per-provider unit pricing (USD). Configurable so admins can update it as providers change.</summary>
public record ProviderPricing
{
    /// <summary>Flat cost per web/shopping/maps search (e.g. SerpAPI plan rate).</summary>
    public decimal PerSearch { get; init; } = 0.005m;

    /// <summary>Cost per page scrape (Context.dev markdown/html ≈ 1 credit).</summary>
    public decimal PerScrape { get; init; } = 0.001m;

    /// <summary>Cost per AI-extract call.</summary>
    public decimal PerExtract { get; init; } = 0.002m;

    /// <summary>Cost per brand lookup (Context.dev ≈ 10 credits).</summary>
    public decimal PerBrandLookup { get; init; } = 0.01m;

    /// <summary>Cost per Google Places text search / details.</summary>
    public decimal PerPlaces { get; init; } = 0.017m;

    /// <summary>Cost per Apify actor run.</summary>
    public decimal PerSocial { get; init; } = 0.01m;

    /// <summary>Cost per Cloudflare browser render.</summary>
    public decimal PerRender { get; init; } = 0.01m;

    /// <summary>Cost per Workers-AI inference call (classify / extract / filter batch).</summary>
    public decimal PerWorkersAi { get; init; } = 0.002m;

    /// <summary>
    /// Cost of the worker HTTP request itself (submit, status, page fetch, search-proxy hop) —
    /// billed on top of the vendor work the worker fronts, never instead of it.
    /// </summary>
    public decimal PerEdgeRequest { get; init; } = 0.0002m;

    /// <summary>Cost of landing one drained edge result (queue pull/ack + the R2 result read).</summary>
    public decimal PerEdgeDrain { get; init; } = 0.0005m;

    /// <summary>Per-million-token LLM rates keyed by model id (input/output).</summary>
    public IReadOnlyDictionary<string, LlmRate> LlmRates { get; init; } = DefaultLlmRates;

    /// <summary>Fallback LLM rate when a model isn't in <see cref="LlmRates"/>.</summary>
    public LlmRate DefaultLlmRate { get; init; } = new(3m, 15m);

    public static readonly IReadOnlyDictionary<string, LlmRate> DefaultLlmRates =
        new Dictionary<string, LlmRate>(StringComparer.OrdinalIgnoreCase)
        {
            ["anthropic/claude-sonnet-4"] = new(3m, 15m),
            ["anthropic/claude-sonnet-5"] = new(3m, 15m),
            ["anthropic/claude-opus-4.1"] = new(15m, 75m),
            ["openai/gpt-4o"] = new(2.5m, 10m),
            ["openai/gpt-4o-mini"] = new(0.15m, 0.6m),
            ["google/gemini-2.5-flash"] = new(0.15m, 0.6m),
            ["google/gemini-2.5-pro"] = new(1.25m, 10m),
            ["meta-llama/llama-3.3-70b-instruct"] = new(0.12m, 0.3m),
            ["deepseek/deepseek-chat"] = new(0.14m, 0.28m),
        };
}

/// <summary>An LLM's price in USD per <em>million</em> input and output tokens.</summary>
public readonly record struct LlmRate(decimal InputPerMillion, decimal OutputPerMillion);

/// <summary>
/// Estimates the USD cost of an external API call from a configurable <see cref="ProviderPricing"/>
/// table. Deterministic and pure — fully unit-testable.
/// </summary>
public sealed class CostEstimator
{
    private readonly ProviderPricing _pricing;

    public CostEstimator(ProviderPricing? pricing = null) => _pricing = pricing ?? new ProviderPricing();

    public ProviderPricing Pricing => _pricing;

    /// <summary>Cost of one LLM completion given the model and token counts.</summary>
    public decimal EstimateLlm(string? model, int? inputTokens, int? outputTokens)
    {
        var rate = model is not null && _pricing.LlmRates.TryGetValue(model, out var r) ? r : _pricing.DefaultLlmRate;
        var input = (inputTokens ?? 0) / 1_000_000m * rate.InputPerMillion;
        var output = (outputTokens ?? 0) / 1_000_000m * rate.OutputPerMillion;
        return decimal.Round(input + output, 6);
    }

    /// <summary>Cost of a non-LLM call, by provider + endpoint.</summary>
    public decimal EstimateCall(string provider, string endpoint)
    {
        var p = provider.ToLowerInvariant();
        var e = endpoint.ToLowerInvariant();

        // Edge-native work has no vendor bill behind it — each is its own line item.
        if (p.StartsWith("workers-ai", StringComparison.Ordinal)) return _pricing.PerWorkersAi;
        if (p == "cloudflare/drain") return _pricing.PerEdgeDrain;

        decimal vendor;
        if (p.Contains("places")) vendor = _pricing.PerPlaces;
        else if (p.Contains("apify") || e.Contains("social")) vendor = _pricing.PerSocial;
        else if (p.Contains("cloudflare") || e.Contains("render")) vendor = _pricing.PerRender;
        else if (p.Contains("context"))
        {
            vendor = e.Contains("brand") ? _pricing.PerBrandLookup
                : e.Contains("extract") ? _pricing.PerExtract
                : _pricing.PerScrape;
        }
        // Structured extraction metered at the ScrapeRouter boundary carries the router's synthetic
        // provider name ("scrape-router"), not the underlying provider — but it is browser-first, so
        // price the "extract" endpoint at the (higher) render rate rather than letting it fall through
        // to PerSearch, which would ~2x under-report browser store extraction and mis-attribute it to
        // SerpAPI spend. (Direct cloudflare-browser / context.dev extracts are already priced above.)
        else if (e.Contains("extract")) vendor = _pricing.PerRender;
        else
        {
            // SerpAPI / Bing and other search engines bill per search.
            vendor = _pricing.PerSearch;
        }

        // A worker-fronted call ("scrape-worker/…", "search-worker/…") still does the vendor work
        // on the edge — the worker request is real extra spend, priced on top of the vendor rate.
        return p.Contains("-worker/") ? vendor + _pricing.PerEdgeRequest : vendor;
    }
}
