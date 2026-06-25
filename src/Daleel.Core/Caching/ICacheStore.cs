namespace Daleel.Core.Caching;

/// <summary>
/// A time-bounded key→value cache for search results. Values are opaque serialized strings
/// (JSON) so the store stays oblivious to what it caches; callers own (de)serialization.
/// </summary>
/// <remarks>
/// Two layers share one store, distinguished only by their key prefix (see <see cref="CacheKey"/>):
/// the <em>provider</em> layer caches one external provider's raw response for an exact
/// provider+query+geo, and the <em>result</em> layer caches a whole normalized query+geo report.
/// Both expire after the same TTL (30 days by default). Implementations must treat an expired
/// entry as absent from <see cref="GetAsync"/> even before <see cref="PurgeExpiredAsync"/> removes it.
/// </remarks>
public interface ICacheStore
{
    /// <summary>Returns the live (unexpired) value for <paramref name="key"/>, or null on miss/expiry.</summary>
    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Stores <paramref name="value"/> under <paramref name="key"/>, replacing any existing
    /// entry, and (re)sets its expiry to now + <paramref name="ttl"/>.</summary>
    Task SetAsync(string key, string value, TimeSpan ttl, CancellationToken cancellationToken = default);

    /// <summary>Deletes all expired entries. Returns the number removed. Safe to call concurrently.</summary>
    Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default);
}
