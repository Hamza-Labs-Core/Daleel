namespace Daleel.Core.Models;

/// <summary>
/// A price for a product observed at a specific store/source at a point in time.
/// </summary>
public record PricePoint
{
    /// <summary>The product the price is for.</summary>
    public string Product { get; init; } = string.Empty;

    /// <summary>The observed price.</summary>
    public Money Price { get; init; }

    /// <summary>Store or site the price was seen at.</summary>
    public string? Store { get; init; }

    /// <summary>Direct link to the listing, when available.</summary>
    public string? Url { get; init; }

    /// <summary>When the price was observed.</summary>
    public DateTimeOffset? ObservedAt { get; init; }

    /// <summary>Whether the listing indicated in-stock availability.</summary>
    public bool? InStock { get; init; }
}
