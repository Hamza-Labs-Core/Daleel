using Daleel.Web.Data;
using Elsa.EntityFrameworkCore.Modules.Management;
using Microsoft.EntityFrameworkCore;

namespace Daleel.Web.Services;

/// <summary>
/// Admin-triggered, irreversible bulk deletes used by the "Data Management" admin page. Unlike the
/// periodic, surgical <see cref="DataCleanupService"/> (which only drops rows failing a quality
/// threshold), this wipes whole tables on demand: the search cache, the harvested catalogue
/// (brands / stores / products / prices / profiles), and Elsa's workflow-instance history.
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
    /// <summary>Deletes every cached search result (both the provider and result cache layers).</summary>
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
        var removed = await _db.SearchCache.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("Admin cleared search cache: {Count} entry(ies) removed", removed);
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
