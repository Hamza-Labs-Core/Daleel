using Daleel.Web.Data;
using Daleel.Web.Pipeline.Enrichment;
using Microsoft.EntityFrameworkCore;

namespace Daleel.Web.Pipeline.Inventory;

/// <summary>
/// The inventory-monitor scheduler: finds monitored stores whose cadence is due and enqueues one
/// inventory.sync unit each, under a SYNTHETIC SearchJob (status "inventory" — the search worker
/// only ever claims "queued", so these jobs ride the queue/timeline machinery without ever being
/// run as searches). The units do the work; this service only decides WHEN.
/// </summary>
public sealed class StoreInventoryMonitorService : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(15);

    /// <summary>SearchJob.Status for synthetic inventory jobs — never claimed by the search worker.</summary>
    public const string JobStatusInventory = "inventory";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<StoreInventoryMonitorService> _logger;

    public StoreInventoryMonitorService(
        IServiceScopeFactory scopeFactory, ILogger<StoreInventoryMonitorService> logger)
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
            try
            {
                await EnqueueDueSyncsAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Inventory monitor scheduler tick failed.");
            }

            try
            {
                await Task.Delay(CheckInterval, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    internal async Task EnqueueDueSyncsAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DaleelDbContext>();
        var queue = scope.ServiceProvider.GetRequiredService<IEnrichmentWorkQueue>();

        var now = DateTimeOffset.UtcNow;
        var due = await db.Stores
            .Where(s => s.MonitorEnabled && s.Website != null && s.Website != "")
            .ToListAsync(ct);
        foreach (var store in due)
        {
            if (store.LastInventorySyncAt is { } last &&
                last.AddHours(Math.Max(1, store.MonitorCadenceHours)) > now)
            {
                continue;
            }

            var domain = DomainOf(store.Website!);
            if (domain is null)
            {
                continue;
            }

            // A still-open previous sync for this store means it's running/settling — skip this tick.
            var openJob = await db.SearchJobs
                .Where(j => j.Status == JobStatusInventory && j.Query == $"inventory:{domain}")
                .OrderByDescending(j => j.Id)
                .FirstOrDefaultAsync(ct);
            if (openJob is not null &&
                await queue.OpenCountAsync(openJob.Id, ct) > 0)
            {
                continue;
            }

            var job = new SearchJob
            {
                UserId = "system:inventory",
                Query = $"inventory:{domain}",
                QueryType = "inventory",
                Geo = "jordan",
                Status = JobStatusInventory,
                CreatedAt = now
            };
            db.SearchJobs.Add(job);
            await db.SaveChangesAsync(ct);

            await queue.EnqueueAsync(new[]
            {
                new EnrichmentWorkItem
                {
                    SearchJobId = job.Id,
                    UserId = job.UserId,
                    Kind = EnrichmentUnit.InventorySync,
                    Payload = EnrichmentWorkQueue.Payload(new InventorySyncPayload(store.Id, domain, now)),
                    MaxAttempts = 3
                }
            }, ct);

            // Stamp optimistically so a crashed sync retries next cadence, not next tick.
            store.LastInventorySyncAt = now;
            await db.SaveChangesAsync(ct);
            _logger.LogInformation("Inventory sync enqueued for {Domain} (store {Id}).", domain, store.Id);
        }
    }

    internal static string? DomainOf(string website)
    {
        var input = website.Contains("://", StringComparison.Ordinal) ? website : $"https://{website}";
        if (!Uri.TryCreate(input, UriKind.Absolute, out var u))
        {
            return null;
        }

        var host = u.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal))
        {
            host = host[4..];
        }

        return host.Length > 0 ? host : null;
    }
}
