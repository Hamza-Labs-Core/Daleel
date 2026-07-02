using Daleel.Web.Data;
using Elsa.EntityFrameworkCore.Modules.Management;
using Microsoft.EntityFrameworkCore;

namespace Daleel.Web.Services;

/// <summary>
/// Admin-triggered, irreversible bulk deletes used by the "Data Management" admin page. Unlike the
/// periodic, surgical <see cref="DataCleanupService"/> (which only drops rows failing a quality
/// threshold), this wipes whole tables on demand: every cached and stored search result (the cache
/// table plus the conversations, jobs, history and saved-result blobs the UI renders), the harvested
/// catalogue (brands / stores / products / prices / profiles), and Elsa's workflow-instance history.
/// </summary>
/// <remarks>
/// Deletes are issued with EF Core's <c>ExecuteDeleteAsync</c> — one <c>DELETE FROM</c> per table that
/// never materialises rows in memory and returns the affected-row count, which we surface back to the
/// admin. The catalogue wipe runs inside a transaction so a mid-way failure can't leave it half-cleared.
/// Child tables are deleted before parents (BrandModels before Brands) so the operation is explicit and
/// countable even though the DB-level <c>ON DELETE CASCADE</c> would otherwise handle dependents.
///
/// Registered TRANSIENT with its own <see cref="DaleelDbContext"/>: it is injected into the interactive
/// admin component, so it must not share the circuit's DbContext with concurrently-rendering components
/// (see the Blazor DbContext-concurrency note in the DI registration).
/// </remarks>
public interface IClearDataService
{
    /// <summary>
    /// Wipes every cached and stored search result so no stale result survives the clear. Covers the
    /// <see cref="SearchCache"/> table (both the provider and result cache layers), the materialized
    /// result blobs that the UI actually renders — the per-user <see cref="UserConversation"/> (the
    /// source of truth every device shows on load), in-flight/finished <see cref="SearchJob"/> rows,
    /// the user's <see cref="SearchHistoryEntry"/> and their bookmarked <see cref="SavedResult"/>s —
    /// all in one transaction. Returns the total rows removed across every table.
    /// </summary>
    Task<int> ClearSearchCacheAsync(CancellationToken ct = default);

    /// <summary>
    /// Deletes the entire harvested catalogue — scraped prices, product profiles, brand models
    /// (cascading their vision-match verdicts), brands and stores — in one transaction.
    /// </summary>
    Task<ClearProductsResult> ClearProductsAsync(CancellationToken ct = default);

    /// <summary>
    /// Deletes all Elsa workflow instances (run history). Returns <c>null</c> when workflow-instance
    /// persistence is not configured (Postgres-only; see Program.cs), so the UI can say so rather than
    /// report "0 deleted".
    /// </summary>
    Task<int?> ClearWorkflowHistoryAsync(CancellationToken ct = default);

    /// <summary>Runs every clear in sequence and reports what each removed.</summary>
    Task<ClearAllResult> ClearAllAsync(CancellationToken ct = default);
}

/// <summary>Per-table counts from a catalogue wipe.</summary>
public sealed record ClearProductsResult(
    int ScrapedPrices,
    int ProductProfiles,
    int Models,
    int Brands,
    int Stores)
{
    public int Total => ScrapedPrices + ProductProfiles + Models + Brands + Stores;
}

/// <summary>What a full "Clear All Data" removed. <see cref="WorkflowInstances"/> is null when Elsa
/// persistence is not configured.</summary>
public sealed record ClearAllResult(
    int SearchCache,
    ClearProductsResult Products,
    int? WorkflowInstances)
{
    public int Total => SearchCache + Products.Total + (WorkflowInstances ?? 0);
}

public sealed class ClearDataService : IClearDataService
{
    private readonly DaleelDbContext _db;
    private readonly IServiceProvider _services;
    private readonly ILogger<ClearDataService> _logger;

    public ClearDataService(DaleelDbContext db, IServiceProvider services, ILogger<ClearDataService> logger)
    {
        _db = db;
        _services = services;
        _logger = logger;
    }

    public async Task<int> ClearSearchCacheAsync(CancellationToken ct = default)
    {
        // Clearing only the cache table is not enough: users kept seeing old results because what the UI
        // renders on load comes from the materialized result blobs (UserConversation.CurrentResultJson —
        // "the source of truth every device renders on load" — plus SearchJob/SearchHistory/SavedResult
        // ResultJson), not from the dedup cache. Wipe all of them together so the clear is total.
        //
        // One transaction so a mid-way failure can't leave results half-cleared (cache gone but the
        // conversation still rendering stale JSON). These tables are independent (SavedResult → SearchHistory
        // is ON DELETE SET NULL, so order doesn't matter for FKs), but the wipe is still all-or-nothing.
        //
        // Note: there is no in-memory result cache to flush — ICacheStore is the DB-backed PostgresCacheStore,
        // and ConversationHub only holds transient per-connection progress signals, not results.
        await using var tx = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        var cache = await _db.SearchCache.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        var conversations = await _db.UserConversations.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        var jobs = await _db.SearchJobs.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        var saved = await _db.SavedResults.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        var history = await _db.SearchHistory.ExecuteDeleteAsync(ct).ConfigureAwait(false);

        await tx.CommitAsync(ct).ConfigureAwait(false);

        var removed = cache + conversations + jobs + saved + history;
        _logger.LogInformation(
            "Admin cleared search results: {Total} row(s) removed ({Cache} cache, {Conversations} conversation(s), " +
            "{Jobs} job(s), {Saved} saved, {History} history)",
            removed, cache, conversations, jobs, saved, history);
        return removed;
    }

    public async Task<ClearProductsResult> ClearProductsAsync(CancellationToken ct = default)
    {
        // One transaction so a failure part-way through can't leave the catalogue half-wiped (e.g. models
        // gone but brands kept). Children first: BrandModels before Brands (FK), and BrandModels cascades
        // its VisionMatchCache verdicts at the DB level.
        await using var tx = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        var scrapedPrices = await _db.ScrapedPrices.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        var productProfiles = await _db.ProductProfiles.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        var models = await _db.BrandModels.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        var brands = await _db.Brands.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        var stores = await _db.Stores.ExecuteDeleteAsync(ct).ConfigureAwait(false);

        await tx.CommitAsync(ct).ConfigureAwait(false);

        var result = new ClearProductsResult(scrapedPrices, productProfiles, models, brands, stores);
        _logger.LogInformation(
            "Admin cleared catalogue: {Models} model(s), {Brands} brand(s), {Stores} store(s), " +
            "{Prices} price(s), {Profiles} profile(s)",
            result.Models, result.Brands, result.Stores, result.ScrapedPrices, result.ProductProfiles);
        return result;
    }

    public async Task<int?> ClearWorkflowHistoryAsync(CancellationToken ct = default)
    {
        // The Elsa management store is registered only when Postgres is configured (Program.cs), so resolve
        // the factory optionally — null means persistence is off and there is nothing to clear.
        var factory = _services.GetService<IDbContextFactory<ManagementElsaDbContext>>();
        if (factory is null)
        {
            _logger.LogInformation("Admin clear workflow history skipped — instance persistence not configured");
            return null;
        }

        await using var elsaDb = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var removed = await elsaDb.WorkflowInstances.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("Admin cleared workflow history: {Count} instance(s) removed", removed);
        return removed;
    }

    public async Task<ClearAllResult> ClearAllAsync(CancellationToken ct = default)
    {
        var cache = await ClearSearchCacheAsync(ct).ConfigureAwait(false);
        var products = await ClearProductsAsync(ct).ConfigureAwait(false);
        var workflows = await ClearWorkflowHistoryAsync(ct).ConfigureAwait(false);
        return new ClearAllResult(cache, products, workflows);
    }
}
