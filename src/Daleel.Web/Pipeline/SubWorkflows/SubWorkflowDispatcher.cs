using Elsa.Workflows;
using Microsoft.Extensions.DependencyInjection;

namespace Daleel.Web.Pipeline.SubWorkflows;

/// <summary>
/// Runs a child Elsa workflow in an isolated DI scope with a hard per-entity timeout, returning the
/// (possibly partial) seeded state. This is the seam the main workflow's dispatch activities use to fan
/// out one sub-workflow per brand/store/item: <see cref="RunManyAsync{TWorkflow,TState,TItem}"/> drives
/// the whole bounded-parallel fan-out, and <see cref="DispatchAsync{TWorkflow,TState}"/> runs a single
/// child.
///
/// Each child gets its own scope (hence its own <c>DaleelDbContext</c> + scoped state), so the children
/// are concurrency-safe. A sub-workflow is best-effort: a timeout or research failure leaves the seeded
/// state untouched (the entity flows through un-enriched) rather than failing the parent search — only a
/// genuine outer cancel (cost cap trip / user cancel) propagates.
/// </summary>
public static class SubWorkflowDispatcher
{
    /// <summary>Hard per-entity budget; a slow brand/store/item is dropped rather than blocking the run.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How many child sub-workflows run at once. Bounds DB/network fan-out per dispatch step.</summary>
    public const int MaxConcurrency = 5;

    /// <summary>
    /// Fans <paramref name="items"/> out through one child <typeparamref name="TWorkflow"/> each, at most
    /// <see cref="MaxConcurrency"/> at a time, each seeded by <paramref name="seed"/> and bounded by
    /// <paramref name="timeout"/>. Returns the finished states in input order.
    /// </summary>
    public static async Task<IReadOnlyList<TState>> RunManyAsync<TWorkflow, TState, TItem>(
        IServiceScopeFactory scopeFactory,
        IReadOnlyList<TItem> items,
        Action<TState, TItem> seed,
        TimeSpan timeout,
        CancellationToken ct)
        where TWorkflow : IWorkflow, new()
        where TState : class
    {
        using var gate = new SemaphoreSlim(MaxConcurrency);
        var tasks = items.Select(async item =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await DispatchAsync<TWorkflow, TState>(
                    scopeFactory, state => seed(state, item), timeout, ct).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        });

        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>Runs one child workflow in a fresh scope; always returns the seeded state, even on timeout.</summary>
    public static async Task<TState> DispatchAsync<TWorkflow, TState>(
        IServiceScopeFactory scopeFactory,
        Action<TState> seed,
        TimeSpan timeout,
        CancellationToken ct)
        where TWorkflow : IWorkflow, new()
        where TState : class
    {
        using var scope = scopeFactory.CreateScope();
        var state = scope.ServiceProvider.GetRequiredService<TState>();
        seed(state);

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
        catch
        {
            // Per-entity timeout or sub-workflow fault: keep the partial state and let the entity flow
            // through un-enriched. One bad brand/store/item must never sink the search.
        }

        // The scope (and its DbContext) disposes here; the state holds only plain DTOs from this point on.
        return state;
    }
}
