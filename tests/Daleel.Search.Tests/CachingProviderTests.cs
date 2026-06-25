using System.Collections.Concurrent;
using Daleel.Core.Caching;
using Daleel.Core.Observability;
using Daleel.Search.Abstractions;
using Daleel.Search.Instrumentation;
using FluentAssertions;
using Xunit;

namespace Daleel.Search.Tests;

/// <summary>In-memory <see cref="ICacheStore"/> for decorator tests (TTL honoured, thread-safe-ish).</summary>
internal sealed class InMemoryCacheStore : ICacheStore
{
    private readonly ConcurrentDictionary<string, (string Value, DateTimeOffset Expires)> _map = new();

    public Task<string?> GetAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(_map.TryGetValue(key, out var e) && e.Expires > DateTimeOffset.UtcNow ? e.Value : null);

    public Task SetAsync(string key, string value, TimeSpan ttl, CancellationToken ct = default)
    {
        _map[key] = (value, DateTimeOffset.UtcNow + ttl);
        return Task.CompletedTask;
    }

    public Task<int> PurgeExpiredAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var removed = 0;
        foreach (var kv in _map)
        {
            if (kv.Value.Expires <= now && _map.TryRemove(kv.Key, out _)) removed++;
        }
        return Task.FromResult(removed);
    }
}

/// <summary>Counts how many times the live provider is actually hit.</summary>
internal sealed class CountingSearchProvider : ISearchProvider
{
    public int Calls { get; private set; }
    public string Name => "counting";
    public bool Supports(SearchKind kind) => true;

    public Task<SearchResults> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult(new SearchResults
        {
            Provider = Name,
            Query = query.Query,
            Kind = query.Kind,
            Results = new[] { new SearchResult { Title = $"hit for {query.Query}", Snippet = "s" } }
        });
    }
}

/// <summary>Collects recorded API calls (here: the synthetic cache hit/miss events).</summary>
internal sealed class RecordingObserver : IApiCallObserver
{
    public List<ApiCall> Calls { get; } = new();
    public void Record(ApiCall call) => Calls.Add(call);
}

public class CachingProviderTests
{
    private static SearchQuery Query(string q) =>
        new() { Query = q, Kind = SearchKind.Web, CountryCode = "jo", LanguageCode = "en" };

    [Fact]
    public async Task SecondIdenticalSearch_ServedFromCache_WithoutHittingProvider()
    {
        var inner = new CountingSearchProvider();
        var observer = new RecordingObserver();
        var cached = CachingProviders.Wrap(inner, new InMemoryCacheStore(), TimeSpan.FromDays(30), observer);

        var first = await cached.SearchAsync(Query("iphone 15"));
        var second = await cached.SearchAsync(Query("  IPHONE  15 ")); // cosmetic variant ⇒ same key

        inner.Calls.Should().Be(1, "the second identical search must come from cache");
        second.Results.Should().BeEquivalentTo(first.Results);

        observer.Calls.Should().HaveCount(2);
        observer.Calls[0].Endpoint.Should().Be("search/miss");
        observer.Calls[1].Endpoint.Should().Be("search/hit");
        observer.Calls.Should().OnlyContain(c => c.Provider == "cache" && c.EstimatedCost == 0m);
    }

    [Fact]
    public async Task DifferentQuery_IsACacheMiss()
    {
        var inner = new CountingSearchProvider();
        var cached = CachingProviders.Wrap(inner, new InMemoryCacheStore(), TimeSpan.FromDays(30));

        await cached.SearchAsync(Query("laptop"));
        await cached.SearchAsync(Query("phone"));

        inner.Calls.Should().Be(2);
    }

    [Fact]
    public async Task ExpiredEntry_IsNotServed()
    {
        var inner = new CountingSearchProvider();
        var cached = CachingProviders.Wrap(inner, new InMemoryCacheStore(), TimeSpan.FromMilliseconds(1));

        await cached.SearchAsync(Query("tv"));
        await Task.Delay(20);
        await cached.SearchAsync(Query("tv"));

        inner.Calls.Should().Be(2, "the entry expired before the second search");
    }
}
