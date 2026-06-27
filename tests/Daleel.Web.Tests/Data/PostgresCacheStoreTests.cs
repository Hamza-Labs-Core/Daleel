using Daleel.Web.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Daleel.Web.Tests.Data;

/// <summary>
/// Exercises <see cref="PostgresCacheStore"/> against real PostgreSQL via a DI container, so the
/// DateTimeOffset expiry comparison and the <c>ExecuteDelete</c> purge run through real SQL translation.
/// </summary>
public sealed class PostgresCacheStoreTests : IDisposable
{
    private readonly PostgresTestContext _ctx = new();
    private readonly ServiceProvider _services;
    private readonly PostgresCacheStore _store;

    public PostgresCacheStoreTests()
    {
        // _ctx created the database and its schema (EnsureCreated); the DI context shares the same DB.
        var sc = new ServiceCollection();
        sc.AddDbContext<DaleelDbContext>(o => o.UseNpgsql(_ctx.ConnectionString));
        _services = sc.BuildServiceProvider();
        _store = new PostgresCacheStore(_services.GetRequiredService<IServiceScopeFactory>());
    }

    [Fact]
    public async Task Set_ThenGet_ReturnsValue()
    {
        await _store.SetAsync("provider:abc", "payload-1", TimeSpan.FromDays(30));

        (await _store.GetAsync("provider:abc")).Should().Be("payload-1");
    }

    [Fact]
    public async Task Set_OnExistingKey_Upserts()
    {
        await _store.SetAsync("result:k", "v1", TimeSpan.FromDays(30));
        await _store.SetAsync("result:k", "v2", TimeSpan.FromDays(30));

        (await _store.GetAsync("result:k")).Should().Be("v2");
        (await CountRowsAsync("result:k")).Should().Be(1, "upsert must not create a duplicate row");
    }

    [Fact]
    public async Task Set_StoresLayerFromKeyPrefix()
    {
        await _store.SetAsync("provider:xyz", "p", TimeSpan.FromDays(30));

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DaleelDbContext>();
        (await db.SearchCache.SingleAsync(c => c.CacheKey == "provider:xyz")).Layer.Should().Be("provider");
    }

    [Fact]
    public async Task Get_ExpiredEntry_ReturnsNull()
    {
        await _store.SetAsync("provider:old", "stale", TimeSpan.FromMilliseconds(1));
        await Task.Delay(30);

        (await _store.GetAsync("provider:old")).Should().BeNull();
    }

    [Fact]
    public async Task PurgeExpired_RemovesOnlyExpiredRows()
    {
        await _store.SetAsync("provider:live", "fresh", TimeSpan.FromDays(30));
        await _store.SetAsync("provider:dead", "stale", TimeSpan.FromMilliseconds(1));
        await Task.Delay(30);

        var removed = await _store.PurgeExpiredAsync();

        removed.Should().Be(1);
        (await _store.GetAsync("provider:live")).Should().Be("fresh");
    }

    private async Task<int> CountRowsAsync(string key)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DaleelDbContext>();
        return await db.SearchCache.CountAsync(c => c.CacheKey == key);
    }

    public void Dispose()
    {
        _services.Dispose();
        _ctx.Dispose();
    }
}
