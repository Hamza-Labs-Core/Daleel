using System.Text.Json;
using Daleel.Agent;
using Daleel.Core.Caching;
using Daleel.Web.Conversation;
using Daleel.Web.Data;
using Daleel.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace Daleel.Web.Pipeline.Enrichment;

/// <summary>
/// The single write path for enrichment output. Each patch row-locks the job, applies one mutation
/// to the CURRENT saved result, and persists before anything is broadcast — so concurrent units
/// never clobber each other and every completed unit's work is durable the moment it finishes.
/// A lost broadcast costs a repaint, never data.
/// </summary>
public interface IEnrichedResultStore
{
    /// <summary>The job's current result, or null when it isn't a product answer.</summary>
    Task<AgentAnswer?> LoadAsync(int jobId, CancellationToken ct = default);

    /// <summary>
    /// Applies <paramref name="mutate"/> to the job's current result under a row lock. Return null
    /// from the mutation to signal "nothing changed" (no write, no broadcast). True when a patch
    /// was persisted.
    /// </summary>
    Task<bool> PatchAsync(
        EnrichmentWorkItem item, Func<AgentAnswer, AgentAnswer?> mutate, CancellationToken ct = default);
}

public sealed class EnrichedResultStore : IEnrichedResultStore
{
    /// <summary>Result-cache TTL — mirrors WorkflowSearchRunner's cache writes.</summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConversationBroadcaster _broadcaster;
    private readonly ICacheStore _cache;
    private readonly ILogger<EnrichedResultStore> _logger;

    public EnrichedResultStore(
        IServiceScopeFactory scopeFactory, IConversationBroadcaster broadcaster, ICacheStore cache,
        ILogger<EnrichedResultStore> logger)
    {
        _scopeFactory = scopeFactory;
        _broadcaster = broadcaster;
        _cache = cache;
        _logger = logger;
    }

    public async Task<AgentAnswer?> LoadAsync(int jobId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DaleelDbContext>();
        var json = await db.SearchJobs.AsNoTracking()
            .Where(j => j.Id == jobId)
            .Select(j => j.ResultJson)
            .FirstOrDefaultAsync(ct);
        return json is null ? null : ResultSerialization.Deserialize<AgentAnswer>(json);
    }

    public async Task<bool> PatchAsync(
        EnrichmentWorkItem item, Func<AgentAnswer, AgentAnswer?> mutate, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<DaleelDbContext>();

        string patchedJson;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            // Row lock serializes concurrent units patching the same job: each mutation sees the
            // PREVIOUS patch's output, so per-item updates compose instead of last-write-wins.
            var job = await db.SearchJobs
                .FromSqlRaw("""SELECT * FROM "SearchJobs" WHERE "Id" = {0} FOR UPDATE""", item.SearchJobId)
                .FirstOrDefaultAsync(ct);

            if (job?.ResultJson is not { } current ||
                ResultSerialization.Deserialize<AgentAnswer>(current) is not { } answer)
            {
                await tx.RollbackAsync(ct);
                return false;
            }

            if (mutate(answer) is not { } patched)
            {
                await tx.RollbackAsync(ct);
                return false;
            }

            patchedJson = ResultSerialization.Serialize(patched);
            job.ResultJson = patchedJson;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        // Durable write is committed — everything below is best-effort propagation (conversation,
        // history, cache, live repaint). Failures are logged, never fatal: the job row is the truth
        // and the next patch (or a reload) re-syncs the reader surfaces.
        try
        {
            await sp.GetRequiredService<IConversationStore>().CompleteAsync(
                item.UserId, item.SearchJobId, "completed", patchedJson, item.ResultType,
                DateTimeOffset.UtcNow, ct);
            await sp.GetRequiredService<ISearchHistoryRepository>()
                .UpdateResultAsync(item.UserId, item.HistoryEntryId, patchedJson, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Enrichment patch for job {JobId}: reader-surface sync failed", item.SearchJobId);
        }

        // The whole-query result VAULT is no longer refreshed (owner decision — the vault was removed; see
        // CheckCacheActivity). The durable per-job record was already patched via the reader surfaces above.
        await _broadcaster.EnrichedAsync(item.UserId, item.SearchJobId, patchedJson, item.ResultType);
        return true;
    }

    /// <summary>
    /// Overwrites the cached result's JSON in place, preserving the entry's moderation stats, so a
    /// repeat search replays the progressively enriched result. Skips silently when no entry exists
    /// (cache disabled or expired) — the cache is an accelerator, not a reader surface.
    /// </summary>
    private async Task RefreshCacheAsync(IServiceProvider sp, int jobId, string patchedJson, CancellationToken ct)
    {
        try
        {
            var db = sp.GetRequiredService<DaleelDbContext>();
            var job = await db.SearchJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, ct);
            if (job is null)
            {
                return;
            }

            var language = string.IsNullOrWhiteSpace(job.Language) ? "en" : job.Language;
            var key = CacheKey.ForResult(job.Query, job.Geo, language);
            if (await _cache.GetAsync(key, ct) is not { } cachedJson ||
                JsonSerializer.Deserialize<CachedSearchResult>(cachedJson) is not { } cached)
            {
                return;
            }

            var updated = cached with { ResultJson = patchedJson };
            await _cache.SetAsync(key, JsonSerializer.Serialize(updated), CacheTtl, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Enrichment patch for job {JobId}: cache refresh failed", jobId);
        }
    }
}
