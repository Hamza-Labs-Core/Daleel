using Daleel.Core.Models;
using Daleel.Search.Abstractions;

namespace Daleel.Search.Providers;

/// <summary>
/// A focused shopping provider that turns a generic <see cref="SearchKind.Shopping"/>
/// search (served by any backing <see cref="ISearchProvider"/>, typically SerpAPI's
/// <c>google_shopping</c> engine) into structured <see cref="PricePoint"/>s and
/// <see cref="StoreResult"/>s.
/// </summary>
/// <remarks>
/// Built by composition rather than inheritance: it holds an underlying
/// <see cref="ISearchProvider"/> so it can be unit-tested with a fake engine, and it
/// works with whatever shopping-capable provider is configured.
/// </remarks>
public sealed class GoogleShoppingProvider
{
    private readonly ISearchProvider _engine;
    private readonly string _defaultCurrency;

    public string Name => "google-shopping";

    public GoogleShoppingProvider(ISearchProvider engine, string defaultCurrency = "USD")
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        if (!engine.Supports(SearchKind.Shopping))
        {
            throw new ArgumentException(
                $"Backing provider '{engine.Name}' does not support shopping search.", nameof(engine));
        }

        _defaultCurrency = defaultCurrency;
    }

    /// <summary>Searches shopping results and returns observed price points.</summary>
    public async Task<IReadOnlyList<PricePoint>> SearchPricesAsync(
        string product,
        string? countryCode = null,
        string? languageCode = null,
        int maxResults = 20,
        CancellationToken cancellationToken = default)
    {
        var results = await _engine.SearchAsync(new SearchQuery
        {
            Query = product,
            Kind = SearchKind.Shopping,
            CountryCode = countryCode,
            LanguageCode = languageCode,
            MaxResults = maxResults
        }, cancellationToken).ConfigureAwait(false);

        var prices = new List<PricePoint>();
        foreach (var r in results.Results)
        {
            if (r.Price is null)
            {
                continue;
            }

            prices.Add(new PricePoint
            {
                Product = string.IsNullOrWhiteSpace(r.Title) ? product : r.Title,
                Price = r.Price.Value,
                Store = r.Seller,
                Url = r.Url
            });
        }

        return prices;
    }

    /// <summary>Searches shopping results and returns them as stores with prices.</summary>
    public async Task<IReadOnlyList<StoreResult>> SearchStoresAsync(
        string product,
        string? countryCode = null,
        string? languageCode = null,
        int maxResults = 20,
        CancellationToken cancellationToken = default)
    {
        var results = await _engine.SearchAsync(new SearchQuery
        {
            Query = product,
            Kind = SearchKind.Shopping,
            CountryCode = countryCode,
            LanguageCode = languageCode,
            MaxResults = maxResults
        }, cancellationToken).ConfigureAwait(false);

        return results.Results.Select(r => new StoreResult
        {
            Name = r.Seller ?? r.Title,
            IsOnline = true,
            Url = r.Url,
            Price = r.Price,
            Source = Name
        }).ToList();
    }
}
