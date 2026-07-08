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
/// Chain order is cheapest/best-first: the primary vendor is tried first, the no-vendor-quota
/// browser scrape is last. Only providers whose <see cref="ISearchProvider.Supports"/> covers the
/// requested <see cref="SearchKind"/> take part, so a Web-only fallback never gets a Shopping query.
/// Each fallback hop invokes the optional reporter — the seam the event spine hooks into.
/// </remarks>
public sealed class SearchRouter : ISearchProvider
{
    private readonly IReadOnlyList<ISearchProvider> _chain;
    private readonly Action<SearchFailover>? _onFailover;

    public string Name => "search-router";

    public SearchRouter(params ISearchProvider[] chain) : this(chain, onFailover: null)
    {
    }

    public SearchRouter(ISearchProvider[] chain, Action<SearchFailover>? onFailover)
    {
        if (chain is null || chain.Length == 0)
        {
            throw new ArgumentException("At least one search provider is required.", nameof(chain));
        }

        _chain = chain;
        _onFailover = onFailover;
    }

    /// <summary>The router serves a kind if ANY member provider does.</summary>
    public bool Supports(SearchKind kind) => _chain.Any(p => p.Supports(kind));

    public async Task<SearchResults> SearchAsync(
        SearchQuery query, CancellationToken cancellationToken = default)
    {
        var supporting = _chain.Where(p => p.Supports(query.Kind)).ToList();
        SearchResults? last = null;

        for (var i = 0; i < supporting.Count; i++)
        {
            var provider = supporting[i];
            SearchResults results;
            string? failReason = null;

            try
            {
                results = await provider.SearchAsync(query, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A vendor failure (e.g. SerpAPI quota → non-2xx → ProviderException) must not fault
                // the search — fall through to the next source exactly as an empty result would.
                failReason = ex.Message;
                results = SearchResults.Empty(provider.Name, query.Query, query.Kind);
            }

            if (failReason is null && results.Results.Count > 0)
            {
                return results;
            }

            last = results;

            // Report the hop only when there is actually a next source to try.
            if (i + 1 < supporting.Count)
            {
                _onFailover?.Invoke(new SearchFailover(
                    provider.Name,
                    supporting[i + 1].Name,
                    query.Kind,
                    query.Query,
                    failReason ?? "no results"));
            }
        }

        return last ?? SearchResults.Empty(Name, query.Query, query.Kind);
    }
}
