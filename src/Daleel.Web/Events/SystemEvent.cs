namespace Daleel.Web.Events;

/// <summary>
/// One row in the unified admin activity timeline — a single thing that happened anywhere in the system:
/// a search lifecycle transition, a pipeline action (provider call, scrape, cache hit, profile save), a
/// user login, a background-job sweep, or an error. Persisted append-only to the event store
/// (<c>daleel_events</c>, alongside <see cref="PipelineEvent"/>) so an operator gets one chronological,
/// searchable feed of everything rather than having to stitch together the per-search firehose, the auth
/// analytics and the background-job logs.
/// </summary>
/// <remarks>
/// Rows are written, never updated. <see cref="DetailsJson"/> is a jsonb column holding event-specific
/// detail, kept free-form so new event kinds need no schema change. Identity only ever enters via
/// <see cref="UserHash"/> — a one-way <see cref="Data.Anonymizer.HashUserId"/> of the user id, never the
/// raw id — so browsing the timeline can't trace a row back to an account.
/// </remarks>
public sealed class SystemEvent
{
    public long Id { get; set; }

    /// <summary>When it happened (UTC; stored as timestamptz).</summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>Coarse bucket the timeline filters by — one of <see cref="SystemEventCategory"/>.</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>Fine-grained type, e.g. "search.completed", "cache.hit", "user.login", "cleanup.swept".</summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>Colour/triage level — one of <see cref="SystemEventSeverity"/> (info | warning | error).</summary>
    public string Severity { get; set; } = SystemEventSeverity.Info;

    /// <summary>The component that emitted it, e.g. "search-worker", "auth", "pipeline/context.dev".</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>One-line human-readable description shown in the feed's main column.</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>Free-form jsonb detail (the expandable row payload). Always valid JSON ("{}" when empty).</summary>
    public string DetailsJson { get; set; } = "{}";

    /// <summary>Correlates every event of one run — the SearchJob/workflow id — or null for ad-hoc events.</summary>
    public string? CorrelationId { get; set; }

    /// <summary>SHA-256 (truncated) hash of the acting user's id, or null. Never the raw id (privacy).</summary>
    public string? UserHash { get; set; }
}

/// <summary>The fixed severities the timeline colour-codes by (blue / amber / red).</summary>
public static class SystemEventSeverity
{
    public const string Info = "info";
    public const string Warning = "warning";
    public const string Error = "error";

    /// <summary>All severities, most-noisy-last (used to seed the filter control).</summary>
    public static readonly IReadOnlyList<string> All = new[] { Info, Warning, Error };
}

/// <summary>
/// The fixed set of timeline categories. These are the user-facing buckets the spec calls out (search,
/// workflow, brand, store, item, cache, LLM, user, background job, error) — coarser than the cost
/// dashboard's <see cref="EventCategory"/>, which is provider-shaped.
/// </summary>
public static class SystemEventCategory
{
    public const string Search = "search";
    public const string Workflow = "workflow";
    public const string Brand = "brand";
    public const string Store = "store";
    public const string Item = "item";
    public const string Cache = "cache";
    public const string Llm = "llm";
    public const string User = "user";
    public const string Maintenance = "maintenance";
    public const string Error = "error";

    /// <summary>All categories, in the order the filter chips render.</summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        Search, Workflow, Brand, Store, Item, Cache, Llm, User, Maintenance, Error
    };
}
