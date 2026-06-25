namespace Daleel.Web.Data;

/// <summary>
/// A persisted, periodically-refreshed brand profile. Built once by the
/// <c>BrandProfileService</c> (which researches the brand via Context.dev + the LLM) and then
/// joined against live search results to enrich them — so a search never re-researches a brand it
/// already knows. Refreshed when <see cref="IsStale"/> (older than the profile TTL, default 30 days).
/// </summary>
/// <remarks>
/// Keyed for upsert by <see cref="NameKey"/> (the normalized brand name) so researching the same
/// brand twice updates the row rather than duplicating it. The list columns (pros/cons/models) are
/// stored as JSON; <see cref="LastRefreshed"/> persists as Unix-ms because SQLite cannot compare
/// <see cref="DateTimeOffset"/> in a WHERE clause and the staleness sweep filters on it.
/// </remarks>
public sealed class Brand
{
    public int Id { get; set; }

    /// <summary>Display name as researched, e.g. "Samsung".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Normalized (trimmed, lower-cased) name — the unique upsert/lookup key.</summary>
    public string NameKey { get; set; } = string.Empty;

    public string? CountryOfOrigin { get; set; }

    /// <summary>Overall reputation on a 0–10 scale (LLM-assessed from gathered reviews).</summary>
    public double? ReputationScore { get; set; }

    public string? Description { get; set; }
    public List<string> Pros { get; set; } = new();
    public List<string> Cons { get; set; } = new();
    public List<string> PopularModels { get; set; } = new();

    /// <summary>Free-text price positioning, e.g. "budget", "mid-to-premium".</summary>
    public string? PriceRange { get; set; }

    public string? Website { get; set; }

    public DateTimeOffset LastRefreshed { get; set; }

    /// <summary>Normalizes a brand name into its lookup key (case/whitespace-insensitive).</summary>
    public static string Normalize(string name) => (name ?? string.Empty).Trim().ToLowerInvariant();

    /// <summary>True when the profile is older than <paramref name="ttl"/> and should be re-researched.</summary>
    public bool IsStale(DateTimeOffset now, TimeSpan ttl) => now - LastRefreshed > ttl;
}
