using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Daleel.Web.Conversation;

/// <summary>
/// In-process work queue for async search jobs, plus a registry of running jobs' cancellation
/// sources so a cancel request can interrupt the worker. Singleton; survives across requests.
/// </summary>
public interface ISearchJobQueue
{
    ValueTask EnqueueAsync(int jobId, CancellationToken ct = default);

    /// <summary>Async stream the background service consumes; completes when the host stops.</summary>
    IAsyncEnumerable<int> ReadAllAsync(CancellationToken ct);

    /// <summary>Registers the running job's token source so it can be cancelled.</summary>
    void Register(int jobId, CancellationTokenSource cts);

    /// <summary>Removes a finished job's registration.</summary>
    void Unregister(int jobId);

    /// <summary>Requests cancellation of a running job. Returns true if it was running.</summary>
    bool RequestCancel(int jobId);
}

public sealed class SearchJobQueue : ISearchJobQueue
{
    private readonly Channel<int> _channel = Channel.CreateUnbounded<int>(
        new UnboundedChannelOptions { SingleReader = true });

    private readonly ConcurrentDictionary<int, CancellationTokenSource> _running = new();

    public ValueTask EnqueueAsync(int jobId, CancellationToken ct = default) =>
        _channel.Writer.WriteAsync(jobId, ct);

    public IAsyncEnumerable<int> ReadAllAsync(CancellationToken ct) =>
        _channel.Reader.ReadAllAsync(ct);

    public void Register(int jobId, CancellationTokenSource cts) => _running[jobId] = cts;

    public void Unregister(int jobId) => _running.TryRemove(jobId, out _);

    public bool RequestCancel(int jobId)
    {
        if (_running.TryGetValue(jobId, out var cts))
        {
            cts.Cancel();
            return true;
        }

        return false;
    }
}
