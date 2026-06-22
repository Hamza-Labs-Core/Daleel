using Daleel.Core.Geo;

namespace Daleel.Core.Models;

/// <summary>
/// A store that sells a product, as surfaced by web/shopping search. Lighter-weight
/// than <see cref="StoreLocation"/> (which is the Google-Places-enriched form).
/// </summary>
public record StoreResult
{
    public string Name { get; init; } = string.Empty;
    public bool IsOnline { get; init; }
    public string? Url { get; init; }
    public string? Address { get; init; }
    public string? Phone { get; init; }
    public Money? Price { get; init; }
    public string? Source { get; init; }
}

/// <summary>
/// A physical place enriched from Google Places: contact, hours, rating, and location.
/// </summary>
public record StoreLocation
{
    public string PlaceId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? Address { get; init; }
    public string? Phone { get; init; }
    public string? Website { get; init; }

    /// <summary>Opening-hours lines as returned by Places (e.g. "Monday: 9 AM–10 PM").</summary>
    public IReadOnlyList<string> OpeningHours { get; init; } = Array.Empty<string>();

    public GeoPoint? Location { get; init; }
    public string? GoogleMapsUrl { get; init; }

    /// <summary>Average rating 1–5.</summary>
    public double? Rating { get; init; }
    public int? ReviewCount { get; init; }

    /// <summary>Google price level 1–4 ($ to $$$$), when provided.</summary>
    public int? PriceLevel { get; init; }

    /// <summary>Distance in metres from the query point, when computed.</summary>
    public double? DistanceMeters { get; init; }

    public IReadOnlyList<StoreReview> Reviews { get; init; } = Array.Empty<StoreReview>();
}

/// <summary>A single Google Places review.</summary>
public record StoreReview
{
    public string? Author { get; init; }
    public double Rating { get; init; }
    public string Text { get; init; } = string.Empty;
    public DateTimeOffset? Date { get; init; }

    /// <summary>BCP-47 language of the review text.</summary>
    public string? Language { get; init; }
}
