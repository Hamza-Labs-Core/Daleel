namespace Daleel.Web.Events;

/// <summary>
/// One recorded action in a search pipeline run — a provider call, scrape, LLM completion, cache
/// hit/miss, or profile lookup. Persisted to the PostgreSQL event store (separate from the
/// app DB) so operators get a durable, queryable audit of every external action and its cost.
/// </summary>
/// <remarks>
/// This is an append-only event: rows are written, never updated. <see cref="MetadataJson"/> is a
/// jsonb column holding event-specific detail (query text, URL, model, token counts, cache key…),
/// kept free-form so new event kinds don't need a schema change.
/// </remarks>
public sealed class PipelineEvent
{
    public long Id { get; set; }

    /// <summary>When the action happened (UTC; stored as timestamptz).</summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>Coarse bucket — one of <see cref="EventCategory"/>.</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>Fine-grained type, e.g. "shopping", "scrape/markdown", "chat", "cache.hit".</summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>External provider, e.g. "SerpAPI", "Context.dev", "Google Places", "OpenRouter/…".</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>Correlates every event of one search run (the SearchJob id), or null for ad-hoc actions.</summary>
    public string? SearchId { get; set; }

    public long DurationMs { get; set; }

    /// <summary>USD cost estimate for this action (0 for free actions like a cache hit).</summary>
    public decimal EstimatedCost { get; set; }

    public bool Success { get; set; } = true;

    /// <summary>Free-form jsonb detail. Always valid JSON ("{}" when empty).</summary>
    public string MetadataJson { get; set; } = "{}";
}

/// <summary>The fixed set of event categories the dashboard buckets by.</summary>
public static class EventCategory
{
    public const string Search = "search";
    public const string Scrape = "scrape";
    public const string Llm = "llm";
    public const string Cache = "cache";
    public const string Profile = "profile";
    public const string Places = "places";
    public const string Extract = "extract";

    /// <summary>All known categories, in display order.</summary>
    public static readonly IReadOnlyList<string> All =
        new[] { Search, Scrape, Extract, Places, Llm, Profile, Cache };
}
