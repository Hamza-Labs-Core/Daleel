using Daleel.Agent;
using Daleel.Web.Data;
using Microsoft.Extensions.Caching.Memory;

namespace Daleel.Web.Services;

/// <summary>Builds the per-search relevance-learning snapshot (flagged negatives) from the RelevanceFlag store.</summary>
public interface IRelevancePolicyProvider
{
    Task<RelevancePolicySnapshot> GetAsync(string query, string? geo, CancellationToken ct = default);
}

/// <summary>
/// Resolves a query's recent "not relevant" flags into a <see cref="RelevancePolicySnapshot"/> fed into the
/// relevance gate. Mirrors <c>ModerationPolicyProvider</c>: own DI scope (never captures a scoped DbContext),
/// a short in-memory cache, and FAIL-OPEN to <see cref="RelevancePolicySnapshot.Empty"/> so a store hiccup
/// can never fault or over-filter a search. Singleton.
/// </summary>
public sealed class RelevancePolicyProvider : IRelevancePolicyProvider
{
    private const int MaxNegatives = 20;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<RelevancePolicyProvider> _logger;

    public RelevancePolicyProvider(
        IServiceScopeFactory scopeFactory, IMemoryCache cache, ILogger<RelevancePolicyProvider> logger)
        => (_scopeFactory, _cache, _logger) = (scopeFactory, cache, logger);

    public async Task<RelevancePolicySnapshot> GetAsync(string query, string? geo, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return RelevancePolicySnapshot.Empty;
        }

        var cacheKey = $"relevance:{RelevanceFlag.QueryKeyOf(query)}:{geo}";
        if (_cache.TryGetValue(cacheKey, out RelevancePolicySnapshot? cached) && cached is not null)
        {
            return cached;
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IRelevanceFlagRepository>();
            var flags = await repo.RecentNegativesAsync(query, geo, MaxNegatives, ct).ConfigureAwait(false);

            var negatives = flags
                .Select(f => new RelevanceNegative(
                    Label: string.Join(" ", new[] { f.Brand, f.Model, f.Name }.Where(s => !string.IsNullOrWhiteSpace(s))),
                    Reason: f.Reason))
                .Where(n => !string.IsNullOrWhiteSpace(n.Label))
                .ToList();

            var snapshot = negatives.Count == 0 ? RelevancePolicySnapshot.Empty : new RelevancePolicySnapshot(negatives);
            _cache.Set(cacheKey, snapshot, CacheTtl);
            return snapshot;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load relevance negatives for '{Query}'; proceeding without them.", query);
            return RelevancePolicySnapshot.Empty;
        }
    }
}
