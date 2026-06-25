using Daleel.Core.Caching;

namespace Daleel.Web.Services;

/// <summary>
/// Sweeps expired <see cref="Daleel.Web.Data.SearchCache"/> rows once a week (plus once at startup),
/// so the cache table doesn't grow unbounded as 30-day entries age out. Best-effort: a failed sweep
/// is logged and retried on the next tick, never crashing the host.
/// </summary>
public sealed class CacheCleanupService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromDays(7);

    private readonly ICacheStore _cache;
    private readonly ILogger<CacheCleanupService> _logger;

    public CacheCleanupService(ICacheStore cache, ILogger<CacheCleanupService> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Sweep once at startup, then on every weekly tick.
        await PurgeAsync(stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await PurgeAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task PurgeAsync(CancellationToken ct)
    {
        try
        {
            var removed = await _cache.PurgeExpiredAsync(ct).ConfigureAwait(false);
            if (removed > 0)
            {
                _logger.LogInformation("Search cache cleanup removed {Count} expired entries", removed);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Host is shutting down — stop quietly.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Search cache cleanup failed; will retry next cycle");
        }
    }
}
