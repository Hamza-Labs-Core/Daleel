using Microsoft.EntityFrameworkCore;

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
    private readonly DaleelDbContext _db;
    public SystemConfigService(DaleelDbContext db) => _db = db;

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
    };

    public async Task<string?> GetAsync(string key, CancellationToken ct = default) =>
        (await _db.SystemConfig.FirstOrDefaultAsync(c => c.Key == key, ct))?.Value;

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
    }

    public async Task<IReadOnlyList<SystemConfig>> AllAsync(CancellationToken ct = default) =>
        await _db.SystemConfig.OrderBy(c => c.Key).ToListAsync(ct);

    /// <summary>Inserts any missing default rows (idempotent; called on startup).</summary>
    public async Task SeedDefaultsAsync(CancellationToken ct = default)
    {
        var existing = await _db.SystemConfig.Select(c => c.Key).ToListAsync(ct);
        var missing = Defaults.Where(d => !existing.Contains(d.Key)).ToList();
        if (missing.Count > 0)
        {
            _db.SystemConfig.AddRange(missing);
            await _db.SaveChangesAsync(ct);
        }
    }
}
