using System.Threading.Channels;

namespace Daleel.Web.Events;

/// <summary>
/// Batches live <see cref="SystemEvent"/>s onto one bounded channel and flushes them to
/// <see cref="ISystemEventLog"/> in the background, so a hot-path <see cref="Enqueue"/> never awaits a DB
/// write. When the channel is full it drops (counted, with a throttled warning) rather than blocking a
/// search — telemetry must never slow or fault the pipeline. Registered as a singleton + hosted service.
/// </summary>
public sealed class SystemEventWriter : BackgroundService
{
    private const int Capacity = 10_000;
    private const int MaxBatch = 256;

    // FullMode.Wait so TryWrite returns false (rather than silently dropping) when full — that's how a
    // full channel is detected and counted. TryWrite itself never blocks regardless of mode.
    private readonly Channel<SystemEvent> _channel = Channel.CreateBounded<SystemEvent>(
        new BoundedChannelOptions(Capacity) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });

    private readonly ISystemEventLog _log;
    private readonly ILogger<SystemEventWriter> _logger;
    private long _dropped;

    public SystemEventWriter(ISystemEventLog log, ILogger<SystemEventWriter> logger)
        => (_log, _logger) = (log, logger);

    /// <summary>Non-blocking enqueue; drops (counted) when the channel is full or the log is disabled. Never throws.</summary>
    public void Enqueue(SystemEvent ev)
    {
        if (!_log.IsEnabled)
        {
            return;
        }

        if (!_channel.Writer.TryWrite(ev))
        {
            var n = Interlocked.Increment(ref _dropped);
            if (n % 1000 == 1)
            {
                _logger.LogWarning("SystemEventWriter channel full — dropped {Count} live events so far.", n);
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reader = _channel.Reader;
        var batch = new List<SystemEvent>(MaxBatch);

        try
        {
            while (await reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
            {
                batch.Clear();
                while (batch.Count < MaxBatch && reader.TryRead(out var ev))
                {
                    batch.Add(ev);
                }

                if (batch.Count > 0)
                {
                    await FlushAsync(batch).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Host shutting down — fall through to drain the tail.
        }

        batch.Clear();
        while (reader.TryRead(out var ev))
        {
            batch.Add(ev);
        }
        if (batch.Count > 0)
        {
            await FlushAsync(batch).ConfigureAwait(false);
        }
    }

    private async Task FlushAsync(List<SystemEvent> batch)
    {
        try
        {
            await _log.PublishManyAsync(batch.ToList(), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Telemetry must never fault a search; swallow + warn (mirrors PostgresSystemEventLog).
            _logger.LogWarning(ex, "Failed to flush {Count} live system events.", batch.Count);
        }
    }
}
