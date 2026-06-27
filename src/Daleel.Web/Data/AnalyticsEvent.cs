using System.ComponentModel.DataAnnotations;

namespace Daleel.Web.Data;

/// <summary>
/// An append-only analytics record. Powers the admin dashboard/analytics without touching the
/// user-owned history/saved tables. IPs are stored hashed, never raw.
/// </summary>
public sealed class AnalyticsEvent
{
    public int Id { get; set; }

    /// <summary>search | login | pageview.</summary>
    [Required]
    public string EventType { get; set; } = string.Empty;

    /// <summary>Actor, when known (null for anonymous page views).</summary>
    public string? UserId { get; set; }

    public string? Query { get; set; }
    public string? QueryType { get; set; }
    public string? Geo { get; set; }
    public string? Model { get; set; }
    public string? Path { get; set; }
    public string? Provider { get; set; }

    /// <summary>SHA-256 (truncated) of the client IP — never the raw address.</summary>
    public string? IpHash { get; set; }

    public int? DurationMs { get; set; }
    public int? ResultCount { get; set; }
    public int? ApiCallsMade { get; set; }

    /// <summary>How many results the halal filter removed during this search.</summary>
    public int? FilteredCount { get; set; }

    /// <summary>Comma-separated categories the filter tripped (alcohol, pork, …) — for moderation stats.</summary>
    public string? FilteredCategories { get; set; }

    /// <summary>UTC timestamp. Stored as DateTime (not DateTimeOffset) — a stable, provider-agnostic encoding the column can still range/group on.</summary>
    public DateTime Timestamp { get; set; }
}

/// <summary>
/// A typed key/value system setting, editable by admins at /admin/settings. Used for rate limits,
/// feature flags, default models per plan, and other tunables.
/// </summary>
public sealed class SystemConfig
{
    [Key]
    public string Key { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    /// <summary>string | int | bool | json — a hint for the editor UI.</summary>
    public string Type { get; set; } = "string";
}
