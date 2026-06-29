using Daleel.Agent;
using Daleel.Core.Intelligence;
using Daleel.Web.Data;
using Daleel.Web.Events;
using Microsoft.EntityFrameworkCore;

namespace Daleel.Web.Services;

/// <summary>
/// Periodically sweeps the saved catalogue — <see cref="Brand"/>, <see cref="Store"/> and
/// <see cref="BrandModel"/> rows — and removes entries that are too thin to be useful or were
/// misclassified during a search (an article saved as a product, a social page saved as a brand).
/// Keeping the catalogue clean matters because the search pipeline JOINs live results against these
/// rows to enrich them; junk rows would surface as junk results.
/// </summary>
/// <remarks>
/// Runs every 6 hours (plus once shortly after startup). The sweep is fully IDEMPOTENT: it only ever
/// deletes rows that fail a fixed data-quality threshold, so a second run over an already-clean table
/// finds nothing to do. Products are swept before brands so a brand whose only models were junk is then
/// correctly seen as product-less and removed in the same pass. Best-effort: a failed sweep is logged
/// and retried on the next tick, never crashing the host.
/// </remarks>
public sealed class DataCleanupService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(3);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DataCleanupService> _logger;

    public DataCleanupService(IServiceScopeFactory scopeFactory, ILogger<DataCleanupService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Let the app finish starting (and any startup migration/seed run) before the first sweep.
            await Task.Delay(StartupDelay, stoppingToken).ConfigureAwait(false);
            await RunAsync(stoppingToken).ConfigureAwait(false);

            using var timer = new PeriodicTimer(Interval);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await RunAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutting down — stop quietly.
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DaleelDbContext>();
            var systemLog = scope.ServiceProvider.GetRequiredService<ISystemEventLog>();

            var report = await SweepAsync(db, ct).ConfigureAwait(false);
            if (report.TotalRemoved > 0)
            {
                // Log for admin visibility: counts per bucket plus a small sample of what went, so a
                // surprising purge can be eyeballed in the logs without a DB query.
                _logger.LogInformation(
                    "Data cleanup removed {Total} row(s): {Products} product(s), {Brands} brand(s), {Stores} store(s). {Samples}",
                    report.TotalRemoved, report.ProductsRemoved, report.BrandsRemoved, report.StoresRemoved,
                    report.SampleLine());
                await systemLog.LogAsync(
                    SystemEventCategory.Maintenance, "cleanup.swept",
                    $"Data cleanup removed {report.TotalRemoved} junk row(s)",
                    source: "data-cleanup",
                    details: new Dictionary<string, object?>
                    {
                        ["products"] = report.ProductsRemoved,
                        ["brands"] = report.BrandsRemoved,
                        ["stores"] = report.StoresRemoved,
                        ["sample"] = report.SampleLine()
                    }, ct: ct);
            }
            else
            {
                _logger.LogDebug("Data cleanup found nothing to remove");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Data cleanup pass failed; will retry next cycle");
            await EmitCleanupFailureAsync(ex);
        }
    }

    /// <summary>Records a cleanup failure to the admin timeline (best-effort, in its own scope).</summary>
    private async Task EmitCleanupFailureAsync(Exception ex)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var systemLog = scope.ServiceProvider.GetRequiredService<ISystemEventLog>();
            await systemLog.LogAsync(
                SystemEventCategory.Maintenance, "cleanup.failed", "Data cleanup pass failed",
                severity: SystemEventSeverity.Error, source: "data-cleanup",
                details: new Dictionary<string, object?>
                {
                    ["error"] = ex.Message, ["exceptionType"] = ex.GetType().Name
                });
        }
        catch
        {
            // The pass already failed and was logged; never let the audit write mask that.
        }
    }

    /// <summary>
    /// The idempotent sweep itself, factored out so it can be exercised directly in tests. Deletes — in
    /// order — junk <see cref="BrandModel"/>s, junk <see cref="Store"/>s, then junk <see cref="Brand"/>s,
    /// committing after products (so brands left product-less by that delete are caught in this same pass).
    /// </summary>
    public static async Task<CleanupReport> SweepAsync(DaleelDbContext db, CancellationToken ct = default)
    {
        var removedProducts = new List<string>();
        var removedStores = new List<string>();
        var removedBrands = new List<string>();

        // ── Products (BrandModel): name + at least one price + a valid source URL, and not an article ──
        var models = await db.BrandModels.ToListAsync(ct).ConfigureAwait(false);
        foreach (var m in models)
        {
            if (!IsKeepableProduct(m))
            {
                db.BrandModels.Remove(m);
                removedProducts.Add(m.ModelName);
            }
        }

        if (removedProducts.Count > 0)
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        // ── Stores: name + (location or website), and not an article/social page ──
        var stores = await db.Stores.ToListAsync(ct).ConfigureAwait(false);
        foreach (var s in stores)
        {
            if (!IsKeepableStore(s))
            {
                db.Stores.Remove(s);
                removedStores.Add(s.Name);
            }
        }

        // ── Brands: name + at least one associated product (a remaining BrandModel or a popular model),
        // and not a social/forum page saved as a brand. Counted AFTER the product delete above so a brand
        // whose only models were just purged is now correctly product-less.
        var brands = await db.Brands.ToListAsync(ct).ConfigureAwait(false);
        var modelCountByBrand = await db.BrandModels
            .GroupBy(m => m.BrandId)
            .Select(g => new { BrandId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.BrandId, x => x.Count, ct)
            .ConfigureAwait(false);

        foreach (var b in brands)
        {
            var associatedProducts = (modelCountByBrand.TryGetValue(b.Id, out var c) ? c : 0) + b.PopularModels.Count;
            if (!IsKeepableBrand(b, associatedProducts))
            {
                db.Brands.Remove(b);
                removedBrands.Add(b.Name);
            }
        }

        if (removedStores.Count > 0 || removedBrands.Count > 0)
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return new CleanupReport(removedProducts, removedBrands, removedStores);
    }

    // ── Data-quality thresholds ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// A product is kept with a valid name and EITHER a real price (local or global, &gt; 0) OR a valid source
    /// URL — and only when it isn't really an article/social page misclassified as a product. The
    /// requirement is deliberately an OR, not an AND: a model discovered from a buying-guide carries a
    /// source page but often no price until the deep-dive scrapes one, and a priced model may have no stored
    /// URL — both are real, useful catalogue rows, so the sweep keeps them and only purges rows with neither
    /// signal (or an article/social page wearing a product's clothes).
    /// </summary>
    internal static bool IsKeepableProduct(BrandModel m) =>
        HasName(m.ModelName)
        && (m.LocalPrice is > 0 || m.GlobalPrice is > 0 || IsValidUrl(m.SourceUrl))
        && !IsEditorialOrSocial(m.SourceUrl, m.ModelName);

    /// <summary>A store is kept with a valid name and a location or website — and isn't an editorial/social page.</summary>
    internal static bool IsKeepableStore(Store s) =>
        HasName(s.Name)
        && (HasText(s.Location) || HasText(s.Address) || IsValidUrl(s.Website))
        && !IsEditorialOrSocial(s.Website, s.Name);

    /// <summary>A brand is kept with a valid name and at least one associated product — and isn't a social/forum page.</summary>
    internal static bool IsKeepableBrand(Brand b, int associatedProductCount) =>
        HasName(b.Name)
        && associatedProductCount > 0
        && !IsEditorialOrSocial(b.Website, b.Name);

    private static bool HasName(string? s) => !string.IsNullOrWhiteSpace(s) && s.Trim().Length >= 2;

    private static bool HasText(string? s) => !string.IsNullOrWhiteSpace(s);

    private static bool IsValidUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && Uri.TryCreate(url, UriKind.Absolute, out var u)
        && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    /// <summary>
    /// True when a row that's stored as a product/store/brand is really an article, review or social/forum
    /// page — the "misclassified item" case. Reuses the same <see cref="ResultClassifier"/> the extraction
    /// pipeline uses (so the cleanup and the extractor agree on what an article is) plus the agent's
    /// non-commerce host list (reddit, youtube, blogspot…).
    /// </summary>
    private static bool IsEditorialOrSocial(string? url, string? title) =>
        AgentService.IsNonCommerceHost(url)
        || ResultClassifier.Classify(url, title, null) == ResultType.ReviewArticle;
}

/// <summary>
/// What a single <see cref="DataCleanupService.SweepAsync"/> removed, by bucket. Names are retained so
/// the service can log a human-readable sample for admin visibility.
/// </summary>
public sealed record CleanupReport(
    IReadOnlyList<string> RemovedProducts,
    IReadOnlyList<string> RemovedBrands,
    IReadOnlyList<string> RemovedStores)
{
    public int ProductsRemoved => RemovedProducts.Count;
    public int BrandsRemoved => RemovedBrands.Count;
    public int StoresRemoved => RemovedStores.Count;
    public int TotalRemoved => ProductsRemoved + BrandsRemoved + StoresRemoved;

    /// <summary>A compact, bounded sample of removed names for a single log line.</summary>
    public string SampleLine()
    {
        var samples = RemovedProducts.Select(n => $"product:{n}")
            .Concat(RemovedBrands.Select(n => $"brand:{n}"))
            .Concat(RemovedStores.Select(n => $"store:{n}"))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Take(10)
            .ToList();
        return samples.Count == 0 ? string.Empty : "e.g. " + string.Join(", ", samples);
    }
}
