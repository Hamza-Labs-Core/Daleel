using Daleel.Core.Llm;
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

    /// <summary>
    /// Default for the LLM-actor step flags: ON where the environment opts in (QA sets
    /// <c>ACTOR_STEPS_DEFAULT=true</c>), OFF everywhere else (production). Read once at load; the seeded
    /// row can still be flipped per-flag at /admin/settings afterwards.
    /// </summary>
    private static readonly string ActorStepsDefault =
        string.Equals(Environment.GetEnvironmentVariable("ACTOR_STEPS_DEFAULT"), "true",
            StringComparison.OrdinalIgnoreCase) ? "true" : "false";

    /// <summary>Defaults seeded on first run; admins can override any of these at /admin/settings.</summary>
    public static readonly IReadOnlyList<SystemConfig> Defaults = new[]
    {
        // LLM-actor steps (guided process, LLM is the actor at each provider-calling step). Seeded so
        // they appear as toggles in Admin → Settings; default ON in QA, OFF in prod. Site discovery
        // (brand/store research) is NOT flagged: it is the only way a site is ever learned — a
        // hostname is never fabricated from an entity's name — so it always runs when unknown.
        new SystemConfig { Key = "actor.itemdive", Value = ActorStepsDefault, Type = "bool" },
        new SystemConfig { Key = "actor.verifypage", Value = ActorStepsDefault, Type = "bool" },
        new SystemConfig { Key = "actor.catalog", Value = "false", Type = "bool" },
        // The model the actor reason→act loops run on — always a CAPABLE model (Sonnet 5 or better),
        // NOT the user's free-tier default (gpt-4o-mini can't sustain the multi-turn action protocol).
        // Admin-editable at /admin/settings.
        new SystemConfig { Key = "actor.model", Value = "anthropic/claude-sonnet-5", Type = "string" },

        new SystemConfig { Key = "ratelimit.page_per_minute", Value = "100", Type = "int" },
        new SystemConfig { Key = "ratelimit.api_per_minute", Value = "10", Type = "int" },
        new SystemConfig { Key = "ratelimit.search_per_hour", Value = "5", Type = "int" },
        new SystemConfig { Key = "feature.export_enabled", Value = "true", Type = "bool" },
        new SystemConfig { Key = "feature.api_access_enabled", Value = "false", Type = "bool" },
        // When false, the search pipeline skips the cache check entirely and every search runs fresh.
        new SystemConfig { Key = "cache.search_enabled", Value = "true", Type = "bool" },
        new SystemConfig { Key = "limit.saved_results_free", Value = "10", Type = "int" },
        new SystemConfig { Key = "model.default_free", Value = "openai/gpt-4o-mini", Type = "string" },
        new SystemConfig { Key = "model.default_pro", Value = "anthropic/claude-sonnet-4", Type = "string" },

        // Per-call-site pipeline model overrides (model.<site>). Each pipeline LLM step resolves its own
        // model from these (see Daleel.Core.Llm.LlmCallSites) so an operator can cost-tune every step
        // independently at /admin/settings; seeded from the registry defaults (a capable model). The
        // enrichment actors read actor.model separately. Generated from the registry so the two never drift.
        new SystemConfig { Key = LlmCallSites.Planner.ConfigKey, Value = LlmCallSites.Planner.DefaultModel, Type = "string" },
        new SystemConfig { Key = LlmCallSites.Category.ConfigKey, Value = LlmCallSites.Category.DefaultModel, Type = "string" },
        new SystemConfig { Key = LlmCallSites.Extraction.ConfigKey, Value = LlmCallSites.Extraction.DefaultModel, Type = "string" },
        new SystemConfig { Key = LlmCallSites.Relevance.ConfigKey, Value = LlmCallSites.Relevance.DefaultModel, Type = "string" },
        new SystemConfig { Key = LlmCallSites.Analyst.ConfigKey, Value = LlmCallSites.Analyst.DefaultModel, Type = "string" },
        new SystemConfig { Key = LlmCallSites.Synthesis.ConfigKey, Value = LlmCallSites.Synthesis.DefaultModel, Type = "string" },
        new SystemConfig { Key = LlmCallSites.BrandReputation.ConfigKey, Value = LlmCallSites.BrandReputation.DefaultModel, Type = "string" },
        new SystemConfig { Key = LlmCallSites.EnrichModel.ConfigKey, Value = LlmCallSites.EnrichModel.DefaultModel, Type = "string" },

        // Per-provider pricing (USD) — drives the CostEstimator; spend is metered + charged to credits,
        // never used to cap/cancel a running job (R1).
        new SystemConfig { Key = "pricing.search", Value = "0.005", Type = "decimal" },
        new SystemConfig { Key = "pricing.scrape", Value = "0.001", Type = "decimal" },
        new SystemConfig { Key = "pricing.extract", Value = "0.002", Type = "decimal" },
        new SystemConfig { Key = "pricing.brand_lookup", Value = "0.01", Type = "decimal" },
        new SystemConfig { Key = "pricing.places", Value = "0.017", Type = "decimal" },
        new SystemConfig { Key = "pricing.social", Value = "0.01", Type = "decimal" },
        new SystemConfig { Key = "pricing.render", Value = "0.01", Type = "decimal" },
        // Edge execution pricing: Workers-AI inference, the worker HTTP hop itself (billed on top of
        // the vendor work it fronts), and the queue+R2 cost of landing one drained result.
        new SystemConfig { Key = "pricing.workers_ai", Value = "0.002", Type = "decimal" },
        new SystemConfig { Key = "pricing.edge_request", Value = "0.0002", Type = "decimal" },
        new SystemConfig { Key = "pricing.edge_drain", Value = "0.0005", Type = "decimal" },

        // Cloudflare execution layer (docs/architecture/cloudflare-workers-pipeline.md). The enable flag
        // routes eligible pipeline work (store catalogue crawls) to the edge workers when the CF_* env
        // endpoints are configured — flipping it back is the instant strangler-fig rollback. The product
        // cap applies to edge catalogue crawls; 0 ⇒ uncapped (the vendor's own ceiling applies).
        new SystemConfig { Key = Cloudflare.CloudflareWorkerOptions.EnabledFlag, Value = "false", Type = "bool" },
        new SystemConfig { Key = Cloudflare.CloudflareWorkerOptions.CatalogMaxProductsKey, Value = "0", Type = "int" },

        // Token authority: rotate worker bearers every N days; 0 = manual-only (/admin/credentials).
        new SystemConfig { Key = Cloudflare.CredentialRotationService.RotationDaysKey, Value = "0", Type = "int" },
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

    /// <summary>
    /// Keys that WERE seeded (or hand-created) but no longer control anything. Insert-only seeding
    /// never removes rows, and /admin/settings renders every row as a live toggle — a switch that
    /// silently does nothing is worse than none, so retired keys are deleted here at startup.
    /// </summary>
    private static readonly string[] RetiredKeys =
    {
        // Site discovery stopped being flag-gated: it is the only way a site is ever learned, so it
        // always runs when the site is unknown (the researcher/BrandResearchHandler own the gating).
        "actor.brandresearch",
        "actor.storeresearch",
        // The per-job cost cap was removed with the uncapped fan-out design; nothing reads these,
        // and an admin setting a "spend ceiling" that silently enforces nothing is a money trap.
        "cost.max_per_job",
        "cost.monthly_alert",
    };

    /// <summary>Inserts any missing default rows and removes retired ones (idempotent; called on startup).</summary>
    public async Task SeedDefaultsAsync(CancellationToken ct = default)
    {
        var retired = await _db.SystemConfig.Where(c => RetiredKeys.Contains(c.Key)).ToListAsync(ct);
        if (retired.Count > 0)
        {
            _db.SystemConfig.RemoveRange(retired);
        }

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
        }

        if (retired.Count > 0 || missing.Count > 0)
        {
            await _db.SaveChangesAsync(ct);
        }
    }
}
