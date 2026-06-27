using Daleel.Core.Caching;
using Microsoft.EntityFrameworkCore;

namespace Daleel.Web.Data;

/// <summary>
/// <see cref="ICacheStore"/> backed by the <see cref="SearchCache"/> table (PostgreSQL). Registered as
/// a singleton and opens a fresh DbContext scope per call, because the agent runs providers in parallel
/// and a scoped <see cref="DaleelDbContext"/> is not safe for concurrent use.
/// </summary>
public sealed class PostgresCacheStore : ICacheStore
{
    private readonly IServiceScopeFactory _scopeFactory;

    public PostgresCacheStore(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DaleelDbContext>();

        var now = DateTimeOffset.UtcNow;
        return await db.SearchCache.AsNoTracking()
            .Where(c => c.CacheKey == key && c.ExpiresAt > now)
            .Select(c => c.Payload)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task SetAsync(string key, string value, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DaleelDbContext>();

        var now = DateTimeOffset.UtcNow;
        var existing = await db.SearchCache.FirstOrDefaultAsync(c => c.CacheKey == key, cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            existing.Payload = value;
            existing.CreatedAt = now;
            existing.ExpiresAt = now + ttl;
        }
        else
        {
            db.SearchCache.Add(new SearchCache
            {
                CacheKey = key,
                Layer = CacheKey.LayerOf(key),
                Payload = value,
                CreatedAt = now,
                ExpiresAt = now + ttl
            });
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // A concurrent writer inserted the same key first — the value is already cached, so the
            // unique-constraint violation is benign. Caching is best-effort; never fail the search.
        }
    }

    public async Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DaleelDbContext>();

        var now = DateTimeOffset.UtcNow;
        return await db.SearchCache
            .Where(c => c.ExpiresAt <= now)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
