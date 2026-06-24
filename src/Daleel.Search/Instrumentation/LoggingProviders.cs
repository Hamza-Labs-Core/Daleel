using System.Text.Json;
using Daleel.Core.Geo;
using Daleel.Core.Models;
using Daleel.Core.Observability;
using Daleel.Core.Pipeline;
using Daleel.Search.Abstractions;

namespace Daleel.Search.Instrumentation;

/// <summary>
/// Decorators that wrap each provider to time, cost-estimate, and report every external call
/// to an <see cref="IApiCallObserver"/> — without the providers themselves knowing about it.
/// The Web layer wraps the providers it builds with these.
/// </summary>
public static class LoggingProviders
{
    public static ISearchProvider Wrap(ISearchProvider inner, IApiCallObserver observer, CostEstimator estimator) =>
        new LoggingSearchProvider(inner, observer, estimator);

    public static IPlacesProvider Wrap(IPlacesProvider inner, IApiCallObserver observer, CostEstimator estimator) =>
        new LoggingPlacesProvider(inner, observer, estimator);

    public static IPostFetcher Wrap(IPostFetcher inner, IApiCallObserver observer, CostEstimator estimator, string provider) =>
        new LoggingPostFetcher(inner, observer, estimator, provider);

    /// <summary>
    /// Wraps a scrape provider, also forwarding <see cref="IExtractProvider"/> when the inner
    /// provider supports it (so Context.dev's AI-Extract stays available and instrumented).
    /// </summary>
    public static IScrapeProvider WrapScrape(IScrapeProvider inner, IApiCallObserver observer, CostEstimator estimator) =>
        inner is IExtractProvider
            ? new LoggingScrapeExtractProvider(inner, observer, estimator)
            : new LoggingScrapeProvider(inner, observer, estimator);
}

internal sealed class LoggingSearchProvider : ISearchProvider
{
    private readonly ISearchProvider _inner;
    private readonly IApiCallObserver _observer;
    private readonly CostEstimator _estimator;

    public LoggingSearchProvider(ISearchProvider inner, IApiCallObserver observer, CostEstimator estimator)
        => (_inner, _observer, _estimator) = (inner, observer, estimator);

    public string Name => _inner.Name;
    public bool Supports(SearchKind kind) => _inner.Supports(kind);

    public Task<SearchResults> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default) =>
        ApiCallTimer.TimeAsync(_observer, _estimator, _inner.Name, query.Kind.ToString().ToLowerInvariant(),
            query.Query, () => _inner.SearchAsync(query, cancellationToken),
            r => r.Results.Sum(x => (long)((x.Title?.Length ?? 0) + (x.Snippet?.Length ?? 0))));
}

internal class LoggingScrapeProvider : IScrapeProvider
{
    protected readonly IScrapeProvider Inner;
    protected readonly IApiCallObserver Observer;
    protected readonly CostEstimator Estimator;

    public LoggingScrapeProvider(IScrapeProvider inner, IApiCallObserver observer, CostEstimator estimator)
        => (Inner, Observer, Estimator) = (inner, observer, estimator);

    public string Name => Inner.Name;

    public Task<ScrapedPage> ScrapeAsync(string url, ScrapeFormat format = ScrapeFormat.Markdown, CancellationToken cancellationToken = default) =>
        ApiCallTimer.TimeAsync(Observer, Estimator, Inner.Name, $"scrape/{format.ToString().ToLowerInvariant()}",
            url, () => Inner.ScrapeAsync(url, format, cancellationToken), p => p.Content?.Length ?? 0);
}

internal sealed class LoggingScrapeExtractProvider : LoggingScrapeProvider, IExtractProvider
{
    private readonly IExtractProvider _extract;

    public LoggingScrapeExtractProvider(IScrapeProvider inner, IApiCallObserver observer, CostEstimator estimator)
        : base(inner, observer, estimator) => _extract = (IExtractProvider)inner;

    public Task<JsonElement> ExtractAsync(string url, object jsonSchema, CancellationToken cancellationToken = default) =>
        ApiCallTimer.TimeAsync(Observer, Estimator, Inner.Name, "extract",
            url, () => _extract.ExtractAsync(url, jsonSchema, cancellationToken), e => e.GetRawText().Length);
}

internal sealed class LoggingPlacesProvider : IPlacesProvider
{
    private readonly IPlacesProvider _inner;
    private readonly IApiCallObserver _observer;
    private readonly CostEstimator _estimator;

    public LoggingPlacesProvider(IPlacesProvider inner, IApiCallObserver observer, CostEstimator estimator)
        => (_inner, _observer, _estimator) = (inner, observer, estimator);

    public string Name => _inner.Name;

    public Task<IReadOnlyList<StoreLocation>> SearchStoresAsync(
        string query, GeoPoint? near = null, double radiusMeters = 5000, string? languageCode = null, CancellationToken cancellationToken = default) =>
        ApiCallTimer.TimeAsync(_observer, _estimator, _inner.Name, "places/text-search",
            query, () => _inner.SearchStoresAsync(query, near, radiusMeters, languageCode, cancellationToken), r => r.Count);

    public Task<IReadOnlyList<StoreLocation>> GetNearbyStoresAsync(
        GeoPoint center, double radiusMeters, string? type = null, CancellationToken cancellationToken = default) =>
        ApiCallTimer.TimeAsync(_observer, _estimator, _inner.Name, "places/nearby",
            type, () => _inner.GetNearbyStoresAsync(center, radiusMeters, type, cancellationToken), r => r.Count);

    public Task<StoreLocation?> GetPlaceDetailsAsync(string placeId, CancellationToken cancellationToken = default) =>
        ApiCallTimer.TimeAsync(_observer, _estimator, _inner.Name, "places/details",
            placeId, () => _inner.GetPlaceDetailsAsync(placeId, cancellationToken));

    public Task<IReadOnlyList<StoreReview>> GetPlaceReviewsAsync(string placeId, CancellationToken cancellationToken = default) =>
        ApiCallTimer.TimeAsync(_observer, _estimator, _inner.Name, "places/reviews",
            placeId, () => _inner.GetPlaceReviewsAsync(placeId, cancellationToken), r => r.Count);
}

internal sealed class LoggingPostFetcher : IPostFetcher
{
    private readonly IPostFetcher _inner;
    private readonly IApiCallObserver _observer;
    private readonly CostEstimator _estimator;
    private readonly string _provider;

    public LoggingPostFetcher(IPostFetcher inner, IApiCallObserver observer, CostEstimator estimator, string provider)
        => (_inner, _observer, _estimator, _provider) = (inner, observer, estimator, provider);

    public Task<IReadOnlyList<SocialPost>> FetchAsync(Source source, string? keyword = null, CancellationToken cancellationToken = default) =>
        ApiCallTimer.TimeAsync(_observer, _estimator, _provider, "social/fetch",
            keyword ?? source.Target, () => _inner.FetchAsync(source, keyword, cancellationToken), r => r.Count);
}
