using System.Text.Json;
using Daleel.Core.Geo;
using Daleel.Core.Models;
using Daleel.Search.Abstractions;
using Daleel.Search.Http;

namespace Daleel.Search.Providers;

/// <summary>
/// Store discovery and enrichment via the Google Places API (New). Supports text search,
/// nearby search, place details, and reviews. Auth is the <c>X-Goog-Api-Key</c> header;
/// the Places API (New) also requires an explicit <c>X-Goog-FieldMask</c> per request.
/// </summary>
/// <remarks>
/// Arabic store names are returned verbatim by Google; matching/dedup against them is
/// handled upstream by <c>ArabicNormalizer</c>. Distances are computed locally with the
/// haversine formula when a query point is supplied, since the API does not always echo
/// a distance.
/// </remarks>
public sealed class GooglePlacesProvider : HttpProviderBase, IPlacesProvider
{
    public const string DefaultBaseUrl = "https://places.googleapis.com";

    private const string DetailFieldMask =
        "id,displayName,formattedAddress,internationalPhoneNumber,websiteUri," +
        "location,googleMapsUri,rating,userRatingCount,priceLevel," +
        "regularOpeningHours.weekdayDescriptions,reviews";

    private const string SearchFieldMask =
        "places.id,places.displayName,places.formattedAddress,places.internationalPhoneNumber," +
        "places.websiteUri,places.location,places.googleMapsUri,places.rating," +
        "places.userRatingCount,places.priceLevel";

    private readonly string _apiKey;
    public string Name => "google-places";
    protected override string ProviderName => Name;

    public GooglePlacesProvider(
        string? apiKey = null,
        HttpClient? httpClient = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
        : base(ConfigureClient(httpClient), maxRetries: 2, delay)
    {
        _apiKey = apiKey
                  ?? Environment.GetEnvironmentVariable("GOOGLE_PLACES_API_KEY")
                  ?? throw new ProviderException("GOOGLE_PLACES_API_KEY is not set.");
    }

    private static HttpClient ConfigureClient(HttpClient? client)
    {
        client ??= new HttpClient();
        client.BaseAddress ??= new Uri(DefaultBaseUrl);
        return client;
    }

    public async Task<IReadOnlyList<StoreLocation>> SearchStoresAsync(
        string query,
        GeoPoint? near = null,
        double radiusMeters = 5000,
        string? languageCode = null,
        CancellationToken cancellationToken = default)
    {
        object body = near is { } p
            ? new
            {
                textQuery = query,
                languageCode,
                locationBias = new
                {
                    circle = new { center = new { latitude = p.Latitude, longitude = p.Longitude }, radius = radiusMeters }
                }
            }
            : new { textQuery = query, languageCode };

        using var doc = await PostAsync("/v1/places:searchText", body, SearchFieldMask, cancellationToken)
            .ConfigureAwait(false);

        return ParsePlaces(doc.RootElement, near);
    }

    public async Task<IReadOnlyList<StoreLocation>> GetNearbyStoresAsync(
        GeoPoint center,
        double radiusMeters,
        string? type = null,
        CancellationToken cancellationToken = default)
    {
        var body = new
        {
            includedTypes = type is null ? null : new[] { type },
            maxResultCount = 20,
            locationRestriction = new
            {
                circle = new
                {
                    center = new { latitude = center.Latitude, longitude = center.Longitude },
                    radius = radiusMeters
                }
            }
        };

        using var doc = await PostAsync("/v1/places:searchNearby", body, SearchFieldMask, cancellationToken)
            .ConfigureAwait(false);

        return ParsePlaces(doc.RootElement, center);
    }

    public async Task<StoreLocation?> GetPlaceDetailsAsync(string placeId, CancellationToken cancellationToken = default)
    {
        using var doc = await SendJsonAsync(
            () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get, $"/v1/places/{Uri.EscapeDataString(placeId)}");
                req.Headers.Add("X-Goog-Api-Key", _apiKey);
                req.Headers.Add("X-Goog-FieldMask", DetailFieldMask);
                return req;
            },
            cancellationToken).ConfigureAwait(false);

        return MapPlace(doc.RootElement, null);
    }

    public async Task<IReadOnlyList<StoreReview>> GetPlaceReviewsAsync(
        string placeId,
        CancellationToken cancellationToken = default)
    {
        var details = await GetPlaceDetailsAsync(placeId, cancellationToken).ConfigureAwait(false);
        return details?.Reviews ?? Array.Empty<StoreReview>();
    }

    private async Task<JsonDocument> PostAsync(
        string path, object body, string fieldMask, CancellationToken cancellationToken) =>
        await SendJsonAsync(
            () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonBody(body) };
                req.Headers.Add("X-Goog-Api-Key", _apiKey);
                req.Headers.Add("X-Goog-FieldMask", fieldMask);
                return req;
            },
            cancellationToken).ConfigureAwait(false);

    private static IReadOnlyList<StoreLocation> ParsePlaces(JsonElement root, GeoPoint? from)
    {
        var list = new List<StoreLocation>();
        if (root.TryGetProperty("places", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var place in arr.EnumerateArray())
            {
                var mapped = MapPlace(place, from);
                if (mapped is not null)
                {
                    list.Add(mapped);
                }
            }
        }

        // When a query point is known, present closest-first.
        return from is not null
            ? list.OrderBy(s => s.DistanceMeters ?? double.MaxValue).ToList()
            : list;
    }

    private static StoreLocation? MapPlace(JsonElement place, GeoPoint? from)
    {
        if (place.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        GeoPoint? location = null;
        if (place.TryGetProperty("location", out var loc) &&
            loc.TryGetProperty("latitude", out var lat) && loc.TryGetProperty("longitude", out var lng))
        {
            location = new GeoPoint(lat.GetDouble(), lng.GetDouble());
        }

        var hours = new List<string>();
        if (place.TryGetProperty("regularOpeningHours", out var oh) &&
            oh.TryGetProperty("weekdayDescriptions", out var wd) && wd.ValueKind == JsonValueKind.Array)
        {
            hours.AddRange(wd.EnumerateArray().Select(x => x.GetString() ?? string.Empty));
        }

        return new StoreLocation
        {
            PlaceId = StrOrNull(place, "id") ?? string.Empty,
            Name = NestedText(place, "displayName", "text") ?? string.Empty,
            Address = StrOrNull(place, "formattedAddress"),
            Phone = StrOrNull(place, "internationalPhoneNumber"),
            Website = StrOrNull(place, "websiteUri"),
            OpeningHours = hours,
            Location = location,
            GoogleMapsUrl = StrOrNull(place, "googleMapsUri"),
            Rating = DblOrNull(place, "rating"),
            ReviewCount = IntOrNull(place, "userRatingCount"),
            PriceLevel = ParsePriceLevel(place),
            DistanceMeters = from is { } f && location is { } l ? Haversine(f, l) : null,
            Reviews = ParseReviews(place)
        };
    }

    private static IReadOnlyList<StoreReview> ParseReviews(JsonElement place)
    {
        if (!place.TryGetProperty("reviews", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<StoreReview>();
        }

        var reviews = new List<StoreReview>();
        foreach (var r in arr.EnumerateArray())
        {
            reviews.Add(new StoreReview
            {
                Author = NestedText(r, "authorAttribution", "displayName"),
                Rating = DblOrNull(r, "rating") ?? 0,
                Text = NestedText(r, "text", "text") ?? NestedText(r, "originalText", "text") ?? string.Empty,
                Language = NestedAttr(r, "text", "languageCode"),
                Date = DateOrNull(r, "publishTime")
            });
        }

        return reviews;
    }

    private static int? ParsePriceLevel(JsonElement place)
    {
        // Places API (New) returns a string enum like "PRICE_LEVEL_MODERATE".
        var raw = StrOrNull(place, "priceLevel");
        return raw switch
        {
            "PRICE_LEVEL_FREE" => 0,
            "PRICE_LEVEL_INEXPENSIVE" => 1,
            "PRICE_LEVEL_MODERATE" => 2,
            "PRICE_LEVEL_EXPENSIVE" => 3,
            "PRICE_LEVEL_VERY_EXPENSIVE" => 4,
            _ => null
        };
    }

    /// <summary>Great-circle distance in metres between two points.</summary>
    internal static double Haversine(GeoPoint a, GeoPoint b)
    {
        const double r = 6_371_000; // Earth radius, metres
        var dLat = ToRad(b.Latitude - a.Latitude);
        var dLng = ToRad(b.Longitude - a.Longitude);
        var lat1 = ToRad(a.Latitude);
        var lat2 = ToRad(b.Latitude);

        var h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(lat1) * Math.Cos(lat2) * Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
        return 2 * r * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }

    private static double ToRad(double deg) => deg * Math.PI / 180.0;

    // ── JSON readers ─────────────────────────────────────────────────────────
    private static string? StrOrNull(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? NestedText(JsonElement e, params string[] path)
    {
        var current = e;
        foreach (var key in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(key, out current))
            {
                return null;
            }
        }
        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    private static string? NestedAttr(JsonElement e, string objKey, string attr) =>
        e.TryGetProperty(objKey, out var obj) && obj.ValueKind == JsonValueKind.Object
            ? StrOrNull(obj, attr) : null;

    private static int? IntOrNull(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    private static double? DblOrNull(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;

    private static DateTimeOffset? DateOrNull(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String &&
        DateTimeOffset.TryParse(v.GetString(), out var dto) ? dto : null;
}
