using System.ComponentModel.DataAnnotations;

namespace Daleel.Web.Data;

/// <summary>
/// One cached search payload, keyed by a normalized hash (see <see cref="Daleel.Core.Caching.CacheKey"/>).
/// Backs <see cref="SqliteCacheStore"/> for both cache layers — the provider layer (one external
/// provider's response) and the result layer (a whole query+geo report) — distinguished by
/// <see cref="Layer"/>. Entries are read until <see cref="ExpiresAt"/>, then swept by the weekly
/// <see cref="Daleel.Web.Services.CacheCleanupService"/>.
/// </summary>
public sealed class SearchCache
{
    public long Id { get; set; }

    /// <summary>Stable hash key, unique across the table. <c>{layer}:{sha256}</c>.</summary>
    [Required]
    public string CacheKey { get; set; } = string.Empty;

    /// <summary>Which cache layer this row belongs to: "provider" or "result".</summary>
    [Required]
    public string Layer { get; set; } = string.Empty;

    /// <summary>Serialized (JSON) cached value.</summary>
    [Required]
    public string Payload { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When this entry stops being served and becomes eligible for purging.</summary>
    public DateTimeOffset ExpiresAt { get; set; }
}
