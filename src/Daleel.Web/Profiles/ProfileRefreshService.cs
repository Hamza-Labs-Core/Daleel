namespace Daleel.Web.Profiles;

/// <summary>
/// Periodically re-researches stale brand/store profiles (older than the TTL) so the saved data the
/// search pipeline joins against stays current without ever refreshing on the request path. Runs
/// daily (plus once shortly after startup). Best-effort: a failed pass is logged and retried.
/// </summary>
/// <remarks>
/// The profile services are scoped (they own a request-scoped <c>DbContext</c>), so each pass opens
/// its own DI scope. No-ops cheaply when no profiles are stale or no research keys are configured.
/// </remarks>
public sealed class ProfileRefreshService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromDays(1);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ProfileOptions _options;
    private readonly ILogger<ProfileRefreshService> _logger;

    public ProfileRefreshService(
        IServiceScopeFactory scopeFactory, ProfileOptions options, ILogger<ProfileRefreshService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Let the app finish starting before the first (potentially LLM-heavy) refresh pass.
            await Task.Delay(StartupDelay, stoppingToken).ConfigureAwait(false);
            await RefreshAsync(stoppingToken).ConfigureAwait(false);

            using var timer = new PeriodicTimer(Interval);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await RefreshAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutting down — stop quietly.
        }
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var brands = scope.ServiceProvider.GetRequiredService<IBrandProfileService>();
            var stores = scope.ServiceProvider.GetRequiredService<IStoreProfileService>();

            var refreshedBrands = await brands.RefreshStaleAsync(_options.MaxRefreshBatch, ct).ConfigureAwait(false);
            var refreshedStores = await stores.RefreshStaleAsync(_options.MaxRefreshBatch, ct).ConfigureAwait(false);

            if (refreshedBrands + refreshedStores > 0)
            {
                _logger.LogInformation(
                    "Profile refresh updated {Brands} brand(s) and {Stores} store(s)",
                    refreshedBrands, refreshedStores);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Profile refresh pass failed; will retry next cycle");
        }
    }
}
