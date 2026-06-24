using Daleel.Core.Intelligence;

namespace Daleel.Core.Models;

/// <summary>
/// Structured, actionable output of a product search (e.g. "search for ACs in Jordan").
/// Unlike <see cref="ProductIntelligence"/> (a narrative + ranked recommendations), this
/// is a directory of concrete things the user can act on right now: priced listings with
/// links, the brands/stores/marketplaces behind them, review sources, and ready-made
/// comparison groups.
/// </summary>
public record ProductSearchResult
{
    /// <summary>The original product query, e.g. "ACs".</summary>
    public string Query { get; init; } = string.Empty;

    /// <summary>Market key the search ran in, e.g. "jordan".</summary>
    public string Geo { get; init; } = string.Empty;

    /// <summary>LLM-generated narrative summarising the market and the picks.</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>Actual products found, with prices and direct links.</summary>
    public IReadOnlyList<ProductListing> Listings { get; init; } = Array.Empty<ProductListing>();

    /// <summary>Brands present in the market (their catalog pages).</summary>
    public IReadOnlyList<BrandInfo> Brands { get; init; } = Array.Empty<BrandInfo>();

    /// <summary>Stores/retailers selling the product.</summary>
    public IReadOnlyList<StoreInfo> Stores { get; init; } = Array.Empty<StoreInfo>();

    /// <summary>Review / buying-guide articles worth reading.</summary>
    public IReadOnlyList<ReviewSource> Reviews { get; init; } = Array.Empty<ReviewSource>();

    /// <summary>Marketplace category pages to browse the full selection.</summary>
    public IReadOnlyList<MarketplaceLink> Marketplaces { get; init; } = Array.Empty<MarketplaceLink>();

    /// <summary>Listings pre-grouped into budget/mid/premium tiers for comparison.</summary>
    public IReadOnlyList<ComparisonGroup> Comparisons { get; init; } = Array.Empty<ComparisonGroup>();

    public DateTimeOffset? GeneratedAt { get; init; }

    /// <summary>True when at least one concrete listing was found.</summary>
    public bool HasListings => Listings.Count > 0;
}

/// <summary>A single concrete product the user can buy, with a direct link.</summary>
public record ProductListing
{
    public string Name { get; init; } = string.Empty;
    public string? Brand { get; init; }
    public string? Model { get; init; }

    /// <summary>Current price amount (null when the source didn't quote one).</summary>
    public decimal? Price { get; init; }

    public string? Currency { get; init; }

    /// <summary>Direct link to the listing.</summary>
    public string? Url { get; init; }

    public string? ImageUrl { get; init; }

    /// <summary>Human-readable source, e.g. "OpenSooq", "Amazon.ae", "Samsung Jordan".</summary>
    public string? Source { get; init; }

    /// <summary>Whether the source is a marketplace, brand site, or store.</summary>
    public ResultType SourceType { get; init; } = ResultType.Unknown;

    /// <summary>Free-form spec key/values, e.g. cooling_capacity, energy_rating.</summary>
    public IReadOnlyDictionary<string, string> Specs { get; init; } =
        new Dictionary<string, string>();

    public string? Availability { get; init; }

    /// <summary>"new" / "used" / "refurbished" when known.</summary>
    public string? Condition { get; init; }

    /// <summary>Pre-discount price, when the listing advertises a deal.</summary>
    public decimal? OriginalPrice { get; init; }

    public string? Seller { get; init; }

    /// <summary>Price as a <see cref="Money"/>, when both amount and currency are present.</summary>
    public Money? AsMoney =>
        Price is { } p && !string.IsNullOrWhiteSpace(Currency) ? new Money(p, Currency!) : null;

    /// <summary>True when the listing advertises a discount off an original price.</summary>
    public bool IsDeal => OriginalPrice is { } o && Price is { } p && o > p;
}

/// <summary>A brand present in the market, pointing at its catalog page.</summary>
public record BrandInfo
{
    public string Name { get; init; } = string.Empty;
    public string? Url { get; init; }
    public string? LogoUrl { get; init; }

    /// <summary>How many of the gathered listings carry this brand.</summary>
    public int ListingCount { get; init; }
}

/// <summary>A store/retailer that sells the product.</summary>
public record StoreInfo
{
    public string Name { get; init; } = string.Empty;
    public string? Url { get; init; }
    public string? Address { get; init; }
    public string? Phone { get; init; }
    public bool IsOnline { get; init; }
}

/// <summary>A review / comparison / buying-guide article.</summary>
public record ReviewSource
{
    public string Title { get; init; } = string.Empty;
    public string? Url { get; init; }
    public string? Snippet { get; init; }
    public string? Source { get; init; }
}

/// <summary>A marketplace category page to browse the full selection on.</summary>
public record MarketplaceLink
{
    public string Name { get; init; } = string.Empty;
    public string? Url { get; init; }

    /// <summary>Number of listings this search surfaced from the marketplace.</summary>
    public int ListingCount { get; init; }
}

/// <summary>
/// A price tier of listings grouped for side-by-side comparison, with a recommendation.
/// </summary>
public record ComparisonGroup
{
    /// <summary>Tier name: "Budget", "Mid-range", or "Premium".</summary>
    public string Category { get; init; } = string.Empty;

    public IReadOnlyList<ProductListing> Items { get; init; } = Array.Empty<ProductListing>();

    /// <summary>The pick within this tier (e.g. best value), as prose.</summary>
    public string? Recommendation { get; init; }
}
