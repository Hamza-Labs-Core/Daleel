namespace Daleel.Core.Models;

/// <summary>
/// A promotion or discounted offer found for a brand or product. Used in brand reports.
/// </summary>
public record DealResult
{
    public string Title { get; init; } = string.Empty;
    public string? Product { get; init; }

    /// <summary>The promotional price, if a concrete figure was found.</summary>
    public Money? Price { get; init; }

    /// <summary>The pre-discount price, when stated.</summary>
    public Money? OriginalPrice { get; init; }

    /// <summary>Discount percentage 0–100, when derivable.</summary>
    public double? DiscountPercent { get; init; }

    public string? Store { get; init; }
    public string? Url { get; init; }
    public DateTimeOffset? Expiry { get; init; }
    public string? Source { get; init; }
}

/// <summary>
/// A richer deal listing used by product-intelligence reports, carrying a computed
/// score (see <c>DealScorer</c>) so listings can be ranked.
/// </summary>
public record DealListing
{
    public string Title { get; init; } = string.Empty;
    public string? Product { get; init; }
    public Money? Price { get; init; }
    public Money? OriginalPrice { get; init; }
    public double? DiscountPercent { get; init; }
    public string? Store { get; init; }
    public string? Url { get; init; }
    public DateTimeOffset? Expiry { get; init; }
    public DateTimeOffset? FoundAt { get; init; }

    /// <summary>Reliability hint for the store (0–1), used in scoring.</summary>
    public double StoreReliability { get; init; } = 0.5;

    /// <summary>Composite rank score assigned by the deal scorer (higher is better).</summary>
    public double Score { get; init; }
}
