using Daleel.Core.Geo;
using Daleel.Core.Models;

namespace Daleel.Search.Abstractions;

/// <summary>
/// Finds physical stores and their details/reviews. Implemented by
/// <c>GooglePlacesProvider</c>.
/// </summary>
public interface IPlacesProvider
{
    string Name { get; }

    /// <summary>
    /// Text search for stores matching <paramref name="query"/>, optionally biased to a
    /// location and radius.
    /// </summary>
    Task<IReadOnlyList<StoreLocation>> SearchStoresAsync(
        string query,
        GeoPoint? near = null,
        double radiusMeters = 5000,
        string? languageCode = null,
        CancellationToken cancellationToken = default);

    /// <summary>Nearby search of a given place type around a point.</summary>
    Task<IReadOnlyList<StoreLocation>> GetNearbyStoresAsync(
        GeoPoint center,
        double radiusMeters,
        string? type = null,
        CancellationToken cancellationToken = default);

    /// <summary>Full details for a single place id.</summary>
    Task<StoreLocation?> GetPlaceDetailsAsync(
        string placeId,
        CancellationToken cancellationToken = default);

    /// <summary>Reviews for a single place id.</summary>
    Task<IReadOnlyList<StoreReview>> GetPlaceReviewsAsync(
        string placeId,
        CancellationToken cancellationToken = default);
}
