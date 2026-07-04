using Daleel.Core.Moderation;
using Daleel.Web.Data;
using Microsoft.Extensions.Caching.Memory;

namespace Daleel.Web.Moderation;

/// <summary>
/// The per-run moderation inputs: admin whitelist keys, feedback-tuned thresholds, and the
/// EFFECTIVE keyword categories (static defaults + dynamic LLM/admin rule overrides), compiled
/// once per snapshot so per-search filters share the regexes.
/// </summary>
public sealed record ModerationPolicySnapshot(
    IReadOnlyCollection<string> WhitelistKeys,
    HalalPolicy Policy,
    IReadOnlyList<ContentFilter.Category> Categories);

/// <summary>
/// Builds the moderation policy each search run uses: the active whitelist (admin "undo"
/// decisions) and per-category classifier thresholds derived from admin correct/incorrect
/// ratings — the feedback loop's read side. Cached briefly so runs don't hammer the tables.
/// </summary>
public interface IModerationPolicyProvider
{
    Task<ModerationPolicySnapshot> GetAsync(CancellationToken ct = default);
}

public sealed class ModerationPolicyProvider : IModerationPolicyProvider
{
    /// <summary>Ratings within this window influence thresholds.</summary>
    private static readonly TimeSpan FeedbackWindow = TimeSpan.FromDays(90);

    /// <summary>How long a computed snapshot is reused before re-reading feedback/whitelist.</summary>
    private static readonly TimeSpan SnapshotTtl = TimeSpan.FromMinutes(5);

    private const string CacheKey = "moderation-policy-snapshot";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<ModerationPolicyProvider> _logger;

    public ModerationPolicyProvider(
        IServiceScopeFactory scopeFactory, IMemoryCache cache, ILogger<ModerationPolicyProvider> logger)
    {
        _scopeFactory = scopeFactory;
        _cache = cache;
        _logger = logger;
    }

    public async Task<ModerationPolicySnapshot> GetAsync(CancellationToken ct = default)
    {
        if (_cache.TryGetValue<ModerationPolicySnapshot>(CacheKey, out var cached) && cached is not null)
        {
            return cached;
        }

        try
        {
            // Own scope: callers run on background job threads where no request scope exists.
            using var scope = _scopeFactory.CreateScope();
            var logs = scope.ServiceProvider.GetRequiredService<IFilteredContentLogRepository>();
            var whitelist = scope.ServiceProvider.GetRequiredService<IModerationWhitelistRepository>();
            var rules = scope.ServiceProvider.GetRequiredService<IModerationRuleRepository>();

            var keys = await whitelist.ActiveKeysAsync(ct).ConfigureAwait(false);
            var stats = await logs.RatingStatsAsync(DateTimeOffset.UtcNow - FeedbackWindow, ct).ConfigureAwait(false);
            var activeRules = await rules.ActiveRulesAsync(ct).ConfigureAwait(false);

            var fallback = new HalalPolicy().DefaultThreshold;
            var thresholds = stats.ToDictionary(
                s => s.Category,
                s => HalalPolicy.ThresholdFromPrecision(s.Correct, s.Incorrect, fallback),
                StringComparer.OrdinalIgnoreCase);

            // Compile the effective keyword set once per snapshot — the dynamic learning surface.
            var categories = ContentFilter.BuildCategories(activeRules
                .Select(r => new ModerationRule(r.Kind, r.Category, r.Term, r.Language))
                .ToArray());

            var snapshot = new ModerationPolicySnapshot(
                keys, new HalalPolicy { CategoryThresholds = thresholds }, categories);
            _cache.Set(CacheKey, snapshot, SnapshotTtl);
            return snapshot;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Policy loading must never block a search: fall back to defaults (empty whitelist,
            // stock thresholds, static categories) and let the next run retry.
            _logger.LogWarning(ex, "Failed to load moderation policy; using defaults for this run");
            return new ModerationPolicySnapshot(
                Array.Empty<string>(), new HalalPolicy(),
                ContentFilter.BuildCategories(Array.Empty<ModerationRule>()));
        }
    }

    /// <summary>Drops the cached snapshot so the next run sees fresh feedback immediately.</summary>
    public static void Invalidate(IMemoryCache cache) => cache.Remove(CacheKey);
}
