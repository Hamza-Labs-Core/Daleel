using System.Text.Json;
using Daleel.Core.Caching;
using Daleel.Core.Observability;
using Daleel.Search.Abstractions;

namespace Daleel.Search.Instrumentation;

/// <summary>
/// Decorator that caches a search provider's response for an exact provider+query+geo, so repeat
/// searches skip the (paid, slow) external call. Wrap this <em>outside</em> the logging decorator:
/// on a cache hit the inner provider — and thus its real-call logging — is never invoked, and the
/// hit/miss is reported to the observer as a synthetic <c>"cache"</c> call instead.
/// </summary>
public static class CachingProviders
{
    /// <param name="ttl">How long a cached response stays valid (e.g. 30 days).</param>
    /// <param name="observer">Optional sink for cache hit/miss telemetry (recorded as a "cache" call).</param>
    public static ISearchProvider Wrap(
        ISearchProvider inner, ICacheStore cache, TimeSpan ttl, IApiCallObserver? observer = null) =>
        new CachingSearchProvider(inner, cache, ttl, observer);
}

internal sealed class CachingSearchProvider : ISearchProvider
{
    // Tolerant on read (cached payloads may predate a field addition); compact on write.
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly ISearchProvider _inner;
    private readonly ICacheStore _cache;
    private readonly TimeSpan _ttl;
    private readonly IApiCallObserver? _observer;

    public CachingSearchProvider(ISearchProvider inner, ICacheStore cache, TimeSpan ttl, IApiCallObserver? observer)
        => (_inner, _cache, _ttl, _observer) = (inner, cache, ttl, observer);

    public string Name => _inner.Name;
    public bool Supports(SearchKind kind) => _inner.Supports(kind);

    public async Task<SearchResults> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        var key = CacheKey.ForProvider(
            _inner.Name, query.Kind.ToString(), query.Query,
            query.CountryCode, query.LanguageCode, query.Location, query.MaxResults);

        // A cache failure must never break search — fall through to the live provider on any error.
        var cached = await TryGetAsync(key, cancellationToken).ConfigureAwait(false);
        if (cached is { } payload && Deserialize(payload) is { } hit)
        {
            RecordCache("hit", query.Query, payload.Length);
            return hit;
        }

        RecordCache("miss", query.Query, 0);
        var fresh = await _inner.SearchAsync(query, cancellationToken).ConfigureAwait(false);
        await TrySetAsync(key, JsonSerializer.Serialize(fresh, Json), cancellationToken).ConfigureAwait(false);
        return fresh;
    }

    private async Task<string?> TryGetAsync(string key, CancellationToken ct)
    {
        try { return await _cache.GetAsync(key, ct).ConfigureAwait(false); }
        catch { return null; }
    }

    private async Task TrySetAsync(string key, string value, CancellationToken ct)
    {
        try { await _cache.SetAsync(key, value, _ttl, ct).ConfigureAwait(false); }
        catch { /* best-effort: a write failure must not fail the search */ }
    }

    private static SearchResults? Deserialize(string payload)
    {
        try { return JsonSerializer.Deserialize<SearchResults>(payload, Json); }
        catch { return null; } // corrupt/old payload ⇒ treat as a miss
    }

    private void RecordCache(string outcome, string query, int bytes) =>
        _observer?.Record(new ApiCall
        {
            Timestamp = DateTimeOffset.UtcNow,
            Provider = "cache",
            Endpoint = $"search/{outcome}",
            RequestSummary = query,
            ResponseBytes = bytes,
            Status = ApiCallStatus.Success,
            EstimatedCost = 0m
        });
}
