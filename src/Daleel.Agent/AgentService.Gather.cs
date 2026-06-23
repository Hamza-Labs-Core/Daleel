using Daleel.Core.Geo;
using Daleel.Core.Models;
using Daleel.Core.Pipeline;
using Daleel.Search.Abstractions;
using Daleel.Search.Moderation;

namespace Daleel.Agent;

/// <summary>
/// The "gather" half of <see cref="AgentService"/>: running a <see cref="SearchStrategy"/> across all
/// configured providers in parallel and bundling the (halal-filtered) results. Split into its own
/// partial file to keep each half of the agent comfortably under the size smell threshold.
/// </summary>
public sealed partial class AgentService
{
    // ── Gather ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Executes a strategy across all configured providers in parallel, normalizes the
    /// social posts with the Arabic matcher, and bundles the results.
    /// </summary>
    public async Task<ResearchBundle> GatherAsync(
        SearchStrategy strategy, GeoProfile geo, CancellationToken cancellationToken = default)
    {
        var webTask = RunSearchAsync(strategy.WebQueries, SearchKind.Web, geo, cancellationToken);
        var shopTask = RunSearchAsync(strategy.ShoppingQueries, SearchKind.Shopping, geo, cancellationToken);
        var placesTask = RunPlacesAsync(strategy.PlacesQueries, geo, geo.Center, cancellationToken);
        var socialTask = RunSocialAsync(strategy, geo, cancellationToken);
        var readTask = RunReadAsync(strategy.UrlsToRead, cancellationToken);

        await Task.WhenAll(webTask, shopTask, placesTask, socialTask, readTask).ConfigureAwait(false);

        // Halal moderation chokepoint: every report builder projects from this bundle, so filtering
        // here removes non-halal web/shopping/store/social content from ALL downstream reports at once.
        var web = _filter.FilterSearchResults(webTask.Result);
        var shopping = _filter.FilterSearchResults(shopTask.Result);
        var stores = _filter.FilterStores(placesTask.Result);
        var social = _filter.FilterSocialPosts(socialTask.Result);
        var pages = readTask.Result;
        LogFilteredCount();

        var prices = shopping
            .Where(r => r.Price is not null)
            .Select(r => new PricePoint
            {
                Product = r.Title,
                Price = r.Price!.Value,
                Store = r.Seller,
                Url = r.Url,
                ObservedAt = _options.Clock()
            })
            .ToList();

        var sources = web.Select(r => r.Url)
            .Concat(shopping.Select(r => r.Url))
            .Concat(pages.Select(p => p.Url))
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Select(u => u!)
            .Distinct()
            .ToList();

        return new ResearchBundle
        {
            Strategy = strategy,
            WebResults = web,
            ShoppingResults = shopping,
            Stores = stores,
            SocialPosts = social,
            Prices = prices,
            Pages = pages,
            Sources = sources
        };
    }

    private async Task<IReadOnlyList<SearchResult>> RunSearchAsync(
        IReadOnlyList<string> queries, SearchKind kind, GeoProfile geo, CancellationToken cancellationToken)
    {
        if (_search is null || !_search.Supports(kind) || queries.Count == 0)
        {
            return Array.Empty<SearchResult>();
        }

        var selected = queries.Take(_options.MaxQueriesPerKind).ToList();
        var tasks = selected.Select(q => SafeSearchAsync(new SearchQuery
        {
            Query = q,
            Kind = kind,
            CountryCode = geo.CountryCode,
            LanguageCode = geo.PrimaryLanguage,
            Location = geo.CenterCity,
            MaxResults = _options.ResultsPerQuery
        }, cancellationToken));

        var batches = await Task.WhenAll(tasks).ConfigureAwait(false);
        return batches.SelectMany(b => b).ToList();
    }

    private async Task<IReadOnlyList<SearchResult>> SafeSearchAsync(SearchQuery query, CancellationToken cancellationToken)
    {
        try
        {
            var results = await _search!.SearchAsync(query, cancellationToken).ConfigureAwait(false);
            return results.Results;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log($"search failed for '{query.Query}': {ex.Message}");
            return Array.Empty<SearchResult>();
        }
    }

    private async Task<IReadOnlyList<StoreLocation>> RunPlacesAsync(
        IReadOnlyList<string> queries, GeoProfile geo, GeoPoint near, CancellationToken cancellationToken)
    {
        if (_places is null || queries.Count == 0)
        {
            return Array.Empty<StoreLocation>();
        }

        var selected = queries.Take(_options.MaxQueriesPerKind).ToList();
        var all = new List<StoreLocation>();
        foreach (var q in selected)
        {
            try
            {
                var stores = await _places.SearchStoresAsync(q, near, 8000, geo.PrimaryLanguage, cancellationToken)
                    .ConfigureAwait(false);
                all.AddRange(stores);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log($"places failed for '{q}': {ex.Message}");
            }
        }

        // Dedupe by place id, keep closest-first.
        return all
            .GroupBy(s => s.PlaceId)
            .Select(g => g.First())
            .OrderBy(s => s.DistanceMeters ?? double.MaxValue)
            .ToList();
    }

    private async Task<IReadOnlyList<SocialPost>> RunSocialAsync(
        SearchStrategy strategy, GeoProfile geo, CancellationToken cancellationToken)
    {
        if (_social is null || strategy.SocialQueries.Count == 0)
        {
            return Array.Empty<SocialPost>();
        }

        var keyword = strategy.SocialQueries[0];
        var actor = geo.ApifyActors.FirstOrDefault();
        var source = new Source
        {
            Name = $"{geo.Key}-social",
            Kind = SourceKind.Search,
            Target = keyword,
            ActorId = actor,
            MaxItems = _options.ResultsPerQuery
        };

        try
        {
            var posts = await _social.FetchAsync(source, keyword, cancellationToken).ConfigureAwait(false);

            // Keep only posts that actually match one of the social keywords (Arabic-aware).
            return posts
                .Where(p => _matcher.Match(p.Text, strategy.SocialQueries, MatchMode.Contains).IsMatch)
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log($"social fetch failed: {ex.Message}");
            return Array.Empty<SocialPost>();
        }
    }

    private async Task<IReadOnlyList<ScrapedPage>> RunReadAsync(
        IReadOnlyList<string> urls, CancellationToken cancellationToken)
    {
        if (_scraper is null || urls.Count == 0)
        {
            return Array.Empty<ScrapedPage>();
        }

        var selected = urls.Take(_options.MaxUrlsToRead).ToList();
        var tasks = selected.Select(async url =>
        {
            try
            {
                return await _scraper.ScrapeAsync(url, ScrapeFormat.Markdown, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log($"scrape failed for '{url}': {ex.Message}");
                return new ScrapedPage { Url = url, Success = false, Error = ex.Message };
            }
        });

        var pages = await Task.WhenAll(tasks).ConfigureAwait(false);
        return pages.Where(p => p.Success).ToList();
    }
}
