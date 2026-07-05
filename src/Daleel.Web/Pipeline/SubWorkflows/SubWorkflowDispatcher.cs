using Elsa.Workflows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Daleel.Web.Pipeline.SubWorkflows;

/// <summary>
/// Runs a child Elsa workflow in an isolated DI scope with a hard per-entity timeout, returning the
/// (possibly partial) seeded state. This is the seam the main workflow's dispatch activities use to fan
/// out one sub-workflow per brand/store/item: <see cref="RunManyAsync{TWorkflow,TState,TItem}"/> drives
/// the whole bounded-parallel fan-out, and <see cref="DispatchAsync{TWorkflow,TState}"/> runs a single
/// child.
///
/// Each child gets its own scope (hence its own <c>DaleelDbContext</c> + scoped state + services), so the
/// children are concurrency-safe. The <paramref name="seed"/> fills both the serializable
/// <typeparamref name="TState"/> and the live <see cref="SubWorkflowServices"/> for that child before it
/// runs. A sub-workflow is best-effort: a timeout or research failure leaves the seeded state untouched
/// (the entity flows through un-enriched) rather than failing the parent search — only a genuine outer
/// cancel (cost cap trip / user cancel) propagates.
/// </summary>
public static class SubWorkflowDispatcher
{
    /// <summary>Hard per-entity budget; a slow brand/store/item is dropped rather than blocking the run.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How many child sub-workflows run at once — a throughput WIDTH (work queues for a slot, nothing
    /// is dropped), tunable via <c>PIPELINE_SUBWORKFLOW_CONCURRENCY</c> (see <see cref="PipelineLimits"/>).
    /// </summary>
    public static int MaxConcurrency => PipelineLimits.SubWorkflowConcurrency;

    /// <summary>
    /// When at least this fraction of the fanned-out children fault, the failure is treated as systematic
    /// (e.g. Context.dev 401 on every call) rather than one bad entity, and surfaced as a WARN-level event.
    /// </summary>
    private const double SystematicFailureRatio = 0.5;

    /// <summary>
    /// Fans <paramref name="items"/> out through one child <typeparamref name="TWorkflow"/> each, at most
    /// <see cref="MaxConcurrency"/> at a time, each seeded by <paramref name="seed"/> (state + services) and
    /// bounded by <paramref name="timeout"/>. Returns the finished states in input order. The parent's
    /// <paramref name="progress"/> sink surfaces a live line if the fan-out fails systematically.
    /// <paramref name="onCompleted"/>, when supplied, streams each entity's finished state (with its input
    /// index) to the caller AS IT LANDS — this is what lets results reach the UI without waiting for the
    /// whole fan-out. The callback is failure-isolated: an exception in it never faults the fan-out.
    /// </summary>
    public static async Task<IReadOnlyList<TState>> RunManyAsync<TWorkflow, TState, TItem>(
        IServiceScopeFactory scopeFactory,
        IReadOnlyList<TItem> items,
        Action<TState, SubWorkflowServices, TItem> seed,
        Action<string>? progress,
        TimeSpan timeout,
        CancellationToken ct,
        Func<int, TState, Task>? onCompleted = null)
        where TWorkflow : IWorkflow, new()
        where TState : SubWorkflowState
    {
        using var gate = new SemaphoreSlim(MaxConcurrency);
        var faults = 0;
        var tasks = items.Select(async (item, index) =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var (state, faulted) = await RunChildAsync<TWorkflow, TState>(
                    scopeFactory, (s, svc) => seed(s, svc, item), timeout, ct).ConfigureAwait(false);
                if (faulted)
                {
                    Interlocked.Increment(ref faults);
                }

                if (onCompleted is not null)
                {
                    try
                    {
                        await onCompleted(index, state).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch
                    {
                        // Streaming is best-effort — a push/merge hiccup must never sink the fan-out.
                    }
                }

                return state;
            }
            finally
            {
                gate.Release();
            }
        });

        var states = await Task.WhenAll(tasks).ConfigureAwait(false);

        // Aggregate-failure signal: per-entity faults are swallowed so one bad brand/store/item can't sink
        // the run — but that also hides a *systematic* failure where every child faults the same way. Count
        // them and, above the threshold, emit a structured WARN event (merged into the parent run's stream)
        // plus a live progress line, so the failure is observable instead of silently "no enrichment".
        if (items.Count > 0 && faults >= Math.Max(2, (int)Math.Ceiling(items.Count * SystematicFailureRatio)))
        {
            progress?.Invoke($"⚠️ {faults}/{items.Count} {typeof(TWorkflow).Name} sub-workflows failed — possible systematic enrichment failure.");
            states.FirstOrDefault()?.RecordEvent("pipeline", "subworkflow_failures", "dispatcher", success: false,
                metadata: new Dictionary<string, object?>
                {
                    ["workflow"] = typeof(TWorkflow).Name,
                    ["failed"] = faults,
                    ["total"] = items.Count
                });
        }

        return states;
    }

    /// <summary>Runs one child workflow in a fresh scope; always returns the seeded state, even on timeout.</summary>
    public static async Task<TState> DispatchAsync<TWorkflow, TState>(
        IServiceScopeFactory scopeFactory,
        Action<TState, SubWorkflowServices> seed,
        TimeSpan timeout,
        CancellationToken ct)
        where TWorkflow : IWorkflow, new()
        where TState : SubWorkflowState
    {
        var (state, _) = await RunChildAsync<TWorkflow, TState>(scopeFactory, seed, timeout, ct)
            .ConfigureAwait(false);
        return state;
    }

    /// <summary>
    /// Core child-runner: returns the (possibly partial) seeded state and whether it faulted. A per-entity
    /// timeout or sub-workflow fault is caught here so the entity flows through un-enriched; only a genuine
    /// outer cancel (cost-cap trip / user cancel) propagates.
    /// </summary>
    private static async Task<(TState State, bool Faulted)> RunChildAsync<TWorkflow, TState>(
        IServiceScopeFactory scopeFactory,
        Action<TState, SubWorkflowServices> seed,
        TimeSpan timeout,
        CancellationToken ct)
        where TWorkflow : IWorkflow, new()
        where TState : SubWorkflowState
    {
        using var scope = scopeFactory.CreateScope();
        var state = scope.ServiceProvider.GetRequiredService<TState>();
        var services = scope.ServiceProvider.GetRequiredService<SubWorkflowServices>();
        seed(state, services);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            var runner = scope.ServiceProvider.GetRequiredService<IWorkflowRunner>();
            await runner.RunAsync(new TWorkflow(), cancellationToken: timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // a real cap-trip / user cancel must stop the whole job
        }
        catch (Exception ex)
        {
            // Per-entity timeout or sub-workflow fault: keep the partial state, let the entity flow through
            // un-enriched, and report the fault so the dispatcher can spot a systematic failure. Log it so a
            // recurring per-entity fault (e.g. every item timing out) is visible instead of silently dropped.
            var timedOut = timeoutCts.IsCancellationRequested;
            scope.ServiceProvider.GetService<ILoggerFactory>()
                ?.CreateLogger(typeof(SubWorkflowDispatcher))
                .Log(timedOut ? LogLevel.Warning : LogLevel.Error, ex,
                    "{Workflow} sub-workflow {Outcome} for an entity; flowing through un-enriched",
                    typeof(TWorkflow).Name, timedOut ? "timed out" : "faulted");
            return (state, true);
        }

        // The scope (and its DbContext) disposes here; the state holds only plain DTOs from this point on.
        return (state, false);
    }
}
