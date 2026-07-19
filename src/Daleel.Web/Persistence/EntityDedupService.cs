using Daleel.Core.Models;
using Daleel.Core.Persistence;
using Daleel.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Daleel.Web.Persistence;

/// <summary>
/// The dedup maintenance worker (spec: docs/superpowers/specs/2026-07-19-entity-dedup-design.md).
/// A recurring pass over the entity index that (1) backfills the tiered <c>IdentityKey</c> on rows
/// saved before it existed, and (2) merges EXACT-key duplicate buckets: the richest row survives,
/// R2 documents union additively, losers become alias rows (<c>MergedIntoId</c>) so old links keep
/// resolving. Merges are evidence-driven only — there is no manual merge path; the ledger
/// (<see cref="EntityMergeLog"/>) is read-only visibility.
/// </summary>
/// <remarks>
/// Off by default (<c>dedup.enabled</c>), dry-run by default (<c>dedup.dry_run</c> — proposed merges
/// are LOGGED but nothing is written). The fuzzy tier (vision/LLM judgment, generic umbrella items)
/// is the spec's stage B and lands separately; this pass only merges rows whose identity keys
/// already collide — the "wrong merge is worse than a duplicate" rule means exact evidence only.
/// </remarks>
public sealed class EntityDedupService : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromHours(6);

    /// <summary>Buckets merged per pass — bounded so a huge backlog drains over runs, not in one.</summary>
    private const int MaxBucketsPerPass = 50;

    /// <summary>IdentityKey backfill batch per pass.</summary>
    private const int BackfillBatch = 500;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EntityDedupService> _logger;

    public EntityDedupService(IServiceScopeFactory scopeFactory, ILogger<EntityDedupService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(StartupDelay, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            var interval = DefaultInterval;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var config = scope.ServiceProvider.GetRequiredService<ISystemConfigService>();
                var minutes = await config.GetIntAsync("dedup.interval_minutes", (int)DefaultInterval.TotalMinutes, ct);
                interval = TimeSpan.FromMinutes(Math.Max(15, minutes));

                if (await config.GetBoolAsync("dedup.enabled", false, ct))
                {
                    var dryRun = await config.GetBoolAsync("dedup.dry_run", true, ct);
                    await RunPassAsync(scope.ServiceProvider, dryRun, ct);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Entity dedup pass failed; next run in {Interval}.", interval);
            }

            try
            {
                await Task.Delay(interval, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>One full pass: backfill, then merge exact-key buckets. Public-shaped for tests via
    /// <see cref="RunOnceAsync"/>.</summary>
    internal async Task RunPassAsync(IServiceProvider services, bool dryRun, CancellationToken ct)
    {
        var db = services.GetRequiredService<DaleelDbContext>();

        var backfilled = await BackfillIdentityKeysAsync(db, ct);
        var merged = await MergeExactBucketsAsync(services, db, dryRun, ct);

        if (backfilled > 0 || merged > 0)
        {
            _logger.LogInformation(
                "Entity dedup pass: {Backfilled} identity key(s) backfilled, {Merged} duplicate(s) {Mode}.",
                backfilled, merged, dryRun ? "proposed (dry-run)" : "merged");
        }
    }

    /// <summary>Testing/manual entry: one pass on a fresh scope.</summary>
    public async Task RunOnceAsync(bool dryRun, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        await RunPassAsync(scope.ServiceProvider, dryRun, ct);
    }

    /// <summary>
    /// Computes <c>IdentityKey</c> for product rows that predate it. Index rows don't store the
    /// model string, so the backfill key is fingerprint-tier (geo + brand + name); rows re-saved by
    /// any later search upgrade to the SKU tier through the save path.
    /// </summary>
    private static async Task<int> BackfillIdentityKeysAsync(DaleelDbContext db, CancellationToken ct)
    {
        var rows = await db.EntityRecords
            .Include(r => r.Brand)
            .Where(r => r.Intent == "Product" && r.IdentityKey == null && r.MergedIntoId == null)
            .OrderBy(r => r.Id)
            .Take(BackfillBatch)
            .ToListAsync(ct);
        foreach (var r in rows)
        {
            r.IdentityKey = StableId.IdentityKeyFor(r.Geo, r.Brand?.Name, model: null, r.Name);
        }

        if (rows.Count > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return rows.Count;
    }

    private async Task<int> MergeExactBucketsAsync(
        IServiceProvider services, DaleelDbContext db, bool dryRun, CancellationToken ct)
    {
        // Buckets of >1 LIVE product rows sharing an identity key — exact-evidence duplicates.
        var keys = await db.EntityRecords
            .Where(r => r.Intent == "Product" && r.MergedIntoId == null && r.IdentityKey != null)
            .GroupBy(r => r.IdentityKey!)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .Take(MaxBucketsPerPass)
            .ToListAsync(ct);
        if (keys.Count == 0)
        {
            return 0;
        }

        var store = services.GetRequiredService<ISearchEntityStore>();
        var now = DateTimeOffset.UtcNow;
        var mergedCount = 0;

        foreach (var key in keys)
        {
            ct.ThrowIfCancellationRequested();
            var bucket = await db.EntityRecords
                .Where(r => r.IdentityKey == key && r.MergedIntoId == null)
                .OrderByDescending(r => r.BrandId != null && r.ProductKey != null)
                .ThenByDescending(r => r.LastRefreshed)
                .ToListAsync(ct);
            if (bucket.Count < 2)
            {
                continue;
            }

            var survivor = bucket[0];
            foreach (var loser in bucket.Skip(1))
            {
                db.EntityMergeLogs.Add(new EntityMergeLog
                {
                    SurvivorId = survivor.Id,
                    LoserId = loser.Id,
                    SurvivorName = survivor.Name,
                    LoserName = loser.Name,
                    Evidence = "identity-key",
                    DryRun = dryRun,
                    CreatedAt = now
                });

                if (dryRun)
                {
                    continue;
                }

                await MergeDocumentsAsync(store, survivor, loser, ct);

                // The loser lives on as an alias: old /product/{id} links resolve; every directory
                // query and future candidate generation skips it. Its R2 doc stays for the undo trail.
                loser.MergedIntoId = survivor.Id;
                mergedCount++;
            }

            await db.SaveChangesAsync(ct);
        }

        return mergedCount;
    }

    /// <summary>
    /// Additive union of the loser's R2 document into the survivor's: offers by Source+Url, images,
    /// pros/cons; specs fill blanks only. Best-effort — an unreadable doc merges nothing (the index
    /// alias still lands, and the loser's doc remains in R2).
    /// </summary>
    private async Task MergeDocumentsAsync(
        ISearchEntityStore store, EntityRecord survivor, EntityRecord loser, CancellationToken ct)
    {
        try
        {
            var sDoc = await store.GetAsync(survivor.Id, SearchIntentType.Product, ct);
            var lDoc = await store.GetAsync(loser.Id, SearchIntentType.Product, ct);
            if (sDoc is null || lDoc is null)
            {
                return;
            }

            var offers = sDoc.Offers.ToList();
            foreach (var o in lDoc.Offers)
            {
                if (!offers.Any(x => string.Equals(x.Source, o.Source, StringComparison.OrdinalIgnoreCase) &&
                                     string.Equals(x.Url, o.Url, StringComparison.OrdinalIgnoreCase)))
                {
                    offers.Add(o);
                }
            }

            var specs = new Dictionary<string, string>(sDoc.Specs, StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in lDoc.Specs)
            {
                specs.TryAdd(k, v);
            }

            var merged = sDoc with
            {
                ImageUrls = sDoc.ImageUrls.Concat(lDoc.ImageUrls).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                ImageUrl = sDoc.ImageUrl ?? lDoc.ImageUrl,
                Offers = offers,
                Specs = specs,
                Pros = sDoc.Pros.Concat(lDoc.Pros).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                Cons = sDoc.Cons.Concat(lDoc.Cons).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                Brand = sDoc.Brand ?? lDoc.Brand,
                Model = sDoc.Model ?? lDoc.Model
            };
            await store.SaveAsync(merged, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Doc merge {Loser} → {Survivor} failed; alias still recorded.", loser.Id, survivor.Id);
        }
    }
}
