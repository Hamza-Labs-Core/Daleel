using Daleel.Web.Storage;

namespace Daleel.Web.Logging;

/// <summary>
/// Hosted loop around <see cref="LogFileMirror"/>: every interval (and once more on shutdown, so the
/// final lines of a stopping container still land), it mirrors the local Serilog day file to R2 in
/// full. Registered only when R2 is configured. See <see cref="LogFileMirror"/> for why this exists
/// instead of the Serilog AmazonS3 sink.
/// </summary>
public sealed class R2LogMirrorService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    private readonly LogFileMirror _mirror;
    private readonly ILogger<R2LogMirrorService> _logger;

    public R2LogMirrorService(IR2StorageService r2, ILogger<R2LogMirrorService> logger)
    {
        _mirror = new LogFileMirror(SerilogConfiguration.DefaultFileLogDirectory, r2);
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await SafeMirrorAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown: one last mirror so the stopping container's final lines reach R2.
            await SafeMirrorAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task SafeMirrorAsync(CancellationToken ct)
    {
        try
        {
            await _mirror.MirrorOnceAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The mirror must never take the host down; a failed pass just retries next interval.
            _logger.LogWarning(ex, "R2 log mirror pass failed");
        }
    }
}
