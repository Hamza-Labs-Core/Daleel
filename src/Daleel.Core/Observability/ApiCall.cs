namespace Daleel.Core.Observability;

/// <summary>Outcome of a single external API call.</summary>
public enum ApiCallStatus
{
    Success,
    Error,
    Timeout
}

/// <summary>
/// A single external API call (LLM, search, scrape, places, social) with the data needed for
/// cost tracking, analytics, and a live activity log: who/what/how-long/how-much.
/// </summary>
public record ApiCall
{
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>Provider name, e.g. "OpenRouter", "SerpAPI", "Context.dev", "Google Places", "Apify".</summary>
    public string Provider { get; init; } = string.Empty;

    /// <summary>Endpoint/action, e.g. "shopping", "scrape/markdown", "chat", "places/text-search".</summary>
    public string Endpoint { get; init; } = string.Empty;

    /// <summary>Short, non-sensitive summary (query text, scraped URL, model).</summary>
    public string? RequestSummary { get; init; }

    public long ResponseTimeMs { get; init; }
    public long ResponseBytes { get; init; }
    public ApiCallStatus Status { get; init; } = ApiCallStatus.Success;

    /// <summary>Estimated cost in USD for this call.</summary>
    public decimal EstimatedCost { get; init; }

    public string? Model { get; init; }
    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }
}

/// <summary>
/// Sink for <see cref="ApiCall"/> records. Implemented at the Web layer to stream the call to
/// the UI, accumulate a running cost total, and persist it. Providers stay oblivious to where
/// the data goes — they just call <see cref="Record"/>.
/// </summary>
public interface IApiCallObserver
{
    void Record(ApiCall call);
}
