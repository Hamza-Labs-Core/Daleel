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

    /// <summary>Removes a finished job's registration (and clears any in-memory cancel flag).</summary>
    void Unregister(int jobId);

    /// <summary>
    /// Requests cancellation of a running job: flips the in-memory cancel flag (so the workflow's
    /// activities bail cooperatively) and cancels its token source. Returns true if it was running on
    /// this instance. The durable <c>SearchJob.CancelRequested</c> column — not this — is the source of
    /// truth; this is the fast same-process path.
    /// </summary>
    bool RequestCancel(int jobId);

    /// <summary>
    /// True while a cancel has been requested for a job still running on this instance. Pipeline
    /// activities poll this to bail out early; the persisted flag + sweep are the durable backstop.
    /// </summary>
    bool IsCancelRequested(int jobId);
}

public sealed class SearchJobQueue : ISearchJobQueue
{
    private readonly Channel<int> _channel = Channel.CreateUnbounded<int>(
        new UnboundedChannelOptions { SingleReader = true });

    private readonly ConcurrentDictionary<int, CancellationTokenSource> _running = new();

    // Cancel-requested ids for jobs running on this instance. Only ever holds running jobs (added on
    // RequestCancel, removed on Unregister), so it can't grow without bound. Source of truth is the
    // persisted SearchJob.CancelRequested column; this is the cheap same-process flag activities poll.
    private readonly ConcurrentDictionary<int, byte> _cancelRequested = new();

    public ValueTask EnqueueAsync(int jobId, CancellationToken ct = default) =>
        _channel.Writer.WriteAsync(jobId, ct);

    public IAsyncEnumerable<int> ReadAllAsync(CancellationToken ct) =>
        _channel.Reader.ReadAllAsync(ct);

    public void Register(int jobId, CancellationTokenSource cts) => _running[jobId] = cts;

    public void Unregister(int jobId)
    {
        _running.TryRemove(jobId, out _);
        _cancelRequested.TryRemove(jobId, out _);
    }

    public bool IsCancelRequested(int jobId) => _cancelRequested.ContainsKey(jobId);

    public bool RequestCancel(int jobId)
    {
        if (_running.TryGetValue(jobId, out var cts))
        {
            // Raise the cooperative flag first so an activity that polls between now and the token firing
            // still sees the cancel, then signal the token. Both are best-effort: the durable flag + sweep
            // guarantee the job actually stops even if the workflow ignores the token entirely.
            _cancelRequested[jobId] = 0;
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The job finished and disposed its CTS in the narrow window after TryGetValue —
                // nothing left to cancel. Treat as a no-op rather than throwing into the caller.
                return false;
            }

            return true;
        }

        return false;
    }
}
