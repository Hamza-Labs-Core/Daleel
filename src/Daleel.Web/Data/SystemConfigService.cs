using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Daleel.Web.Data;

/// <summary>Typed access to admin-editable <see cref="SystemConfig"/> settings (rate limits, flags…).</summary>
public interface ISystemConfigService
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);
    Task<int> GetIntAsync(string key, int fallback, CancellationToken ct = default);
    Task<bool> GetBoolAsync(string key, bool fallback, CancellationToken ct = default);
    Task SetAsync(string key, string value, string type = "string", CancellationToken ct = default);
    Task<IReadOnlyList<SystemConfig>> AllAsync(CancellationToken ct = default);

    /// <summary>Inserts any missing default rows (idempotent; called on startup).</summary>
    Task SeedDefaultsAsync(CancellationToken ct = default);
}

public sealed class SystemConfigService : ISystemConfigService
{
    private const string CacheKey = "systemconfig:snapshot";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    private readonly DaleelDbContext _db;
    private readonly IMemoryCache? _cache;

    // The cache is optional so unit tests can `new SystemConfigService(db)`; in the app DI supplies
    // the registered IMemoryCache so hot-path readers (the rate-limit middleware) avoid a DB hit.
    public SystemConfigService(DaleelDbContext db, IMemoryCache? cache = null)
    {
        _db = db;
        _cache = cache;
    }

    /// <summary>All config as a key→value map, cached briefly and evicted on <see cref="SetAsync"/>.</summary>
    private async Task<IReadOnlyDictionary<string, string>> SnapshotAsync(CancellationToken ct)
    {
        if (_cache is null)
        {
            return await LoadAsync(ct);
        }

        if (_cache.TryGetValue(CacheKey, out IReadOnlyDictionary<string, string>? cached) && cached is not null)
        {
            return cached;
        }

        var loaded = await LoadAsync(ct);
        _cache.Set(CacheKey, loaded, CacheTtl);
        return loaded;
    }

    private async Task<IReadOnlyDictionary<string, string>> LoadAsync(CancellationToken ct) =>
        await _db.SystemConfig.AsNoTracking().ToDictionaryAsync(c => c.Key, c => c.Value, ct);

    /// <summary>Defaults seeded on first run; admins can override any of these at /admin/settings.</summary>
    public static readonly IReadOnlyList<SystemConfig> Defaults = new[]
    {
        new SystemConfig { Key = "ratelimit.page_per_minute", Value = "100", Type = "int" },
        new SystemConfig { Key = "ratelimit.api_per_minute", Value = "10", Type = "int" },
        new SystemConfig { Key = "ratelimit.search_per_hour", Value = "5", Type = "int" },
        new SystemConfig { Key = "feature.export_enabled", Value = "true", Type = "bool" },
        new SystemConfig { Key = "feature.api_access_enabled", Value = "false", Type = "bool" },
        new SystemConfig { Key = "limit.saved_results_free", Value = "10", Type = "int" },
        new SystemConfig { Key = "model.default_free", Value = "openai/gpt-4o-mini", Type = "string" },
        new SystemConfig { Key = "model.default_pro", Value = "anthropic/claude-sonnet-4", Type = "string" },

        // Cost controls + per-provider pricing (USD). max_per_job = 0 ⇒ no cap.
        new SystemConfig { Key = "cost.max_per_job", Value = "0", Type = "decimal" },
        new SystemConfig { Key = "cost.monthly_alert", Value = "50", Type = "decimal" },
        new SystemConfig { Key = "pricing.search", Value = "0.005", Type = "decimal" },
        new SystemConfig { Key = "pricing.scrape", Value = "0.001", Type = "decimal" },
        new SystemConfig { Key = "pricing.extract", Value = "0.002", Type = "decimal" },
        new SystemConfig { Key = "pricing.brand_lookup", Value = "0.01", Type = "decimal" },
        new SystemConfig { Key = "pricing.places", Value = "0.017", Type = "decimal" },
        new SystemConfig { Key = "pricing.social", Value = "0.01", Type = "decimal" },
        new SystemConfig { Key = "pricing.render", Value = "0.01", Type = "decimal" },
    };

    public async Task<string?> GetAsync(string key, CancellationToken ct = default) =>
        (await SnapshotAsync(ct)).TryGetValue(key, out var v) ? v : null;

    public async Task<int> GetIntAsync(string key, int fallback, CancellationToken ct = default) =>
        int.TryParse(await GetAsync(key, ct), out var v) ? v : fallback;

    public async Task<bool> GetBoolAsync(string key, bool fallback, CancellationToken ct = default) =>
        bool.TryParse(await GetAsync(key, ct), out var v) ? v : fallback;

    public async Task SetAsync(string key, string value, string type = "string", CancellationToken ct = default)
    {
        var row = await _db.SystemConfig.FirstOrDefaultAsync(c => c.Key == key, ct);
        if (row is null)
        {
            _db.SystemConfig.Add(new SystemConfig { Key = key, Value = value, Type = type });
        }
        else
        {
            row.Value = value;
            row.Type = type;
        }

        await _db.SaveChangesAsync(ct);
        _cache?.Remove(CacheKey); // invalidate so the next read reflects the admin's change immediately
    }

    public async Task<IReadOnlyList<SystemConfig>> AllAsync(CancellationToken ct = default) =>
        await _db.SystemConfig.OrderBy(c => c.Key).ToListAsync(ct);

    /// <summary>Inserts any missing default rows (idempotent; called on startup).</summary>
    public async Task SeedDefaultsAsync(CancellationToken ct = default)
    {
        var existing = await _db.SystemConfig.Select(c => c.Key).ToListAsync(ct);
        // Insert COPIES, never the shared static Defaults instances — otherwise EF would track the
        // static objects and a later SetAsync mutation would corrupt the process-wide defaults.
        var missing = Defaults
            .Where(d => !existing.Contains(d.Key))
            .Select(d => new SystemConfig { Key = d.Key, Value = d.Value, Type = d.Type })
            .ToList();
        if (missing.Count > 0)
        {
            _db.SystemConfig.AddRange(missing);
            await _db.SaveChangesAsync(ct);
        }
    }
}
