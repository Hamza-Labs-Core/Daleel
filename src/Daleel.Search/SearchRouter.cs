using Daleel.Search.Abstractions;

namespace Daleel.Search;

/// <summary>
/// One failover hop inside a <see cref="SearchRouter"/> chain: the provider that came up short and
/// the one being tried next. Surfaced so the pipeline can REPORT the hop (progress line now, a
/// persisted discovery event later) instead of silently degrading when SerpAPI is exhausted.
/// </summary>
public readonly record struct SearchFailover(
    string FromProvider, string ToProvider, SearchKind Kind, string Query, string Reason);

/// <summary>
/// Routes a discovery search through an ordered list of <see cref="ISearchProvider"/>s, falling
/// back to the next when one throws or returns no results. The search-side analogue of
/// <see cref="ScrapeRouter"/>: configured SerpAPI → Bing → browser-SERP, so a vendor outage (most
/// pressingly SerpAPI monthly-quota exhaustion, which returns non-2xx → <c>ProviderException</c>)
/// fails OVER to a quota-free source instead of degrading web discovery to a silent empty.
/// </summary>
/// <remarks>
/// Chain order is cheapest/best-first, but the failover is <b>hedged, not strictly sequential</b>:
/// the primary is started first, and a fallback is spun up early — as soon as the primary either
/// comes up short OR simply takes longer than <see cref="HedgeDelay"/> — instead of blocking on the
/// primary's full retry/timeout cycle before the next source is even tried. Whoever returns usable
/// results first wins and the losers are cancelled, so a slow primary degrades to the fallback in
/// ~one hedge delay rather than in minutes. A zero hedge delay races every supporting provider at
/// once. Only providers whose <see cref="ISearchProvider.Supports"/> covers the requested
/// <see cref="SearchKind"/> take part. Each hop still invokes the optional reporter — the seam the
/// event spine hooks into.
/// </remarks>
public sealed class SearchRouter : ISearchProvider
{
    /// <summary>
    /// How long to wait for the current in-flight provider(s) before hedging to the next source when
    /// none has failed yet. Short enough that a slow primary degrades near-instantly; long enough
    /// that the common fast-primary path never needlessly spends a fallback call (which for SerpAPI
    /// is the metered hourly cap). Override with <c>SEARCH_FALLBACK_HEDGE_MS</c> (clamped 0–30000ms;
    /// 0 = race every provider immediately).
    /// </summary>
    public static readonly TimeSpan DefaultHedgeDelay = TimeSpan.FromMilliseconds(2500);

    private readonly IReadOnlyList<ISearchProvider> _chain;
    private readonly Action<SearchFailover>? _onFailover;
    private readonly TimeSpan _hedgeDelay;

    public string Name => "search-router";

    /// <summary>The hedge delay this router will apply before spinning up the next source.</summary>
    public TimeSpan HedgeDelay => _hedgeDelay;

    public SearchRouter(params ISearchProvider[] chain) : this(chain, onFailover: null)
    {
    }

    public SearchRouter(ISearchProvider[] chain, Action<SearchFailover>? onFailover, TimeSpan? hedgeDelay = null)
    {
        if (chain is null || chain.Length == 0)
        {
            throw new ArgumentException("At least one search provider is required.", nameof(chain));
        }

        _chain = chain;
        _onFailover = onFailover;
        _hedgeDelay = hedgeDelay ?? ResolveHedgeDelay();
    }

    private static TimeSpan ResolveHedgeDelay()
    {
        var raw = Environment.GetEnvironmentVariable("SEARCH_FALLBACK_HEDGE_MS");
        var ms = int.TryParse(raw, out var parsed) && parsed >= 0 ? parsed : (int)DefaultHedgeDelay.TotalMilliseconds;
        return TimeSpan.FromMilliseconds(Math.Clamp(ms, 0, 30_000));
    }

    /// <summary>The router serves a kind if ANY member provider does.</summary>
    public bool Supports(SearchKind kind) => _chain.Any(p => p.Supports(kind));

    /// <summary>One provider's finished run: its results and, when it came up short, why.</summary>
    private readonly record struct Attempt(int Index, SearchResults Results, string? FailReason)
    {
        public bool IsGood => FailReason is null && Results.Results.Count > 0;
    }

    public async Task<SearchResults> SearchAsync(
        SearchQuery query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var supporting = _chain.Where(p => p.Supports(query.Kind)).ToList();
        if (supporting.Count == 0)
        {
            return SearchResults.Empty(Name, query.Query, query.Kind);
        }

        // Losers are cancelled the moment a winner is found (or on any exit), so a slow/failed
        // provider that has already been overtaken stops burning a metered call.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            var inflight = new List<Task<Attempt>>();
            Attempt? lastShort = null;
            var nextToStart = 0;

            // Start the primary; subsequent sources are hedged in as the primary lags or falls short.
            inflight.Add(RunAsync(nextToStart, supporting[nextToStart], query, cts.Token));
            nextToStart++;

            while (inflight.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Race the in-flight providers against a hedge timer (only while sources remain to start).
                var moreToStart = nextToStart < supporting.Count;
                Task? hedge = moreToStart ? Task.Delay(_hedgeDelay, cts.Token) : null;
                var finished = await Task.WhenAny(
                    hedge is null ? inflight : inflight.Cast<Task>().Append(hedge)).ConfigureAwait(false);

                if (finished == hedge)
                {
                    // The current source(s) are slow but not yet failed — hedge to the next one now
                    // instead of waiting out its full retry/timeout cycle.
                    Report(supporting, nextToStart - 1, nextToStart, query, $"slow (> {_hedgeDelay.TotalMilliseconds:0}ms)");
                    inflight.Add(RunAsync(nextToStart, supporting[nextToStart], query, cts.Token));
                    nextToStart++;
                    continue;
                }

                var attemptTask = (Task<Attempt>)finished;
                inflight.Remove(attemptTask);
                var attempt = await attemptTask.ConfigureAwait(false); // already completed

                if (attempt.IsGood)
                {
                    return attempt.Results; // finally cancels the losers
                }

                // This source came up short — remember it and, if any source is still untried, start
                // the next one immediately (don't wait out the hedge once we KNOW this one failed).
                lastShort = attempt;
                if (nextToStart < supporting.Count)
                {
                    Report(supporting, attempt.Index, nextToStart, query, attempt.FailReason ?? "no results");
                    inflight.Add(RunAsync(nextToStart, supporting[nextToStart], query, cts.Token));
                    nextToStart++;
                }
            }

            // Nobody produced usable results. Genuine outer cancellation still surfaces as a throw.
            cancellationToken.ThrowIfCancellationRequested();
            return lastShort?.Results ?? SearchResults.Empty(Name, query.Query, query.Kind);
        }
        finally
        {
            cts.Cancel(); // tear down any provider still running once we have an answer (or are bailing)
        }
    }

    /// <summary>
    /// Runs one provider, mapping any non-cancellation failure or empty payload into a "short"
    /// <see cref="Attempt"/> so the race treats a throw exactly like an empty result. A cancellation
    /// (a peer already won, or the outer caller cancelled) also collapses to a short attempt; the
    /// caller re-checks the OUTER token before returning so real outer cancellation propagates.
    /// </summary>
    private static async Task<Attempt> RunAsync(
        int index, ISearchProvider provider, SearchQuery query, CancellationToken cancellationToken)
    {
        try
        {
            var results = await provider.SearchAsync(query, cancellationToken).ConfigureAwait(false);
            // An empty outcome may carry its own cause (e.g. the edge worker's capped soft-empty
            // SERP) — surface that as the failover reason instead of the generic "no results".
            return new Attempt(index, results,
                results.Results.Count > 0 ? null : results.Diagnostic ?? "no results");
        }
        catch (OperationCanceledException)
        {
            return new Attempt(index, SearchResults.Empty(provider.Name, query.Query, query.Kind), "cancelled");
        }
        catch (Exception ex)
        {
            return new Attempt(index, SearchResults.Empty(provider.Name, query.Query, query.Kind), ex.Message);
        }
    }

    private void Report(IReadOnlyList<ISearchProvider> supporting, int from, int to, SearchQuery query, string reason) =>
        _onFailover?.Invoke(new SearchFailover(
            supporting[from].Name, supporting[to].Name, query.Kind, query.Query, reason));
}
