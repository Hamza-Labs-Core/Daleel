using Daleel.Core.Intelligence;

namespace Daleel.Core.Models;

/// <summary>
/// One place a specific product model is available, with its price and a direct link. A
/// <see cref="ProductModel"/> aggregates many of these so a model is listed once with all
/// its sources rather than repeated per store.
/// </summary>
public record PriceOffer
{
    /// <summary>Human-readable source, e.g. a marketplace or store name.</summary>
    public string Source { get; init; } = string.Empty;
    public ResultType SourceType { get; init; } = ResultType.Unknown;

    public decimal? Price { get; init; }
    public string? Currency { get; init; }
    public string? Url { get; init; }

    /// <summary>"new" / "used" / "refurbished" when known.</summary>
    public string? Condition { get; init; }
    public string? Availability { get; init; }
    public string? Seller { get; init; }

    /// <summary>Pre-discount price, when the offer advertises a deal.</summary>
    public decimal? OriginalPrice { get; init; }

    /// <summary>Whether this offer is confirmed local to the target market.</summary>
    public bool IsLocal { get; init; }

    /// <summary>Whether the offer advertises free shipping.</summary>
    public bool FreeShipping { get; init; }

    /// <summary>Set by the aggregator: this is the cheapest offer for the model.</summary>
    public bool IsLowest { get; init; }

    public Money? AsMoney =>
        Price is { } p && !string.IsNullOrWhiteSpace(Currency) ? new Money(p, Currency!) : null;

    public bool IsDeal => OriginalPrice is { } o && Price is { } p && o > p;

    /// <summary>Short deal/badge tags for the price panel ("LOWEST", "SALE", "FREE SHIPPING").</summary>
    public IReadOnlyList<string> Tags
    {
        get
        {
            var tags = new List<string>();
            if (IsLowest) tags.Add("LOWEST");
            if (IsDeal) tags.Add("SALE");
            if (FreeShipping) tags.Add("FREE SHIPPING");
            return tags;
        }
    }
}

/// <summary>
/// A distinct product model with everything known about it: identity, specs, images, an
/// optional MSRP and LLM-distilled pros/cons, plus every place it's available aggregated
/// into <see cref="Offers"/>. This is the unit the product grid and detail panel render.
/// </summary>
public record ProductModel
{
    public string Name { get; init; } = string.Empty;
    public string? Brand { get; init; }
    public string? Model { get; init; }
    public string? ProductLine { get; init; }
    public string? ImageUrl { get; init; }

    public IReadOnlyDictionary<string, string> Specs { get; init; } = new Dictionary<string, string>();

    /// <summary>Official manufacturer suggested retail price, when found.</summary>
    public decimal? Msrp { get; init; }
    public string? MsrpCurrency { get; init; }

    public IReadOnlyList<string> Pros { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Cons { get; init; } = Array.Empty<string>();

    /// <summary>LLM-generated pros/cons summary distilled from reviews.</summary>
    public string? ReviewSummary { get; init; }

    /// <summary>Reputation of this model's brand in the target market, when assessed.</summary>
    public BrandReputation? BrandReputation { get; init; }

    /// <summary>Every place the model is available, sorted cheapest-first.</summary>
    public IReadOnlyList<PriceOffer> Offers { get; init; } = Array.Empty<PriceOffer>();

    public PriceOffer? LowestOffer => Offers.FirstOrDefault(o => o.Price is not null) ?? Offers.FirstOrDefault();

    /// <summary>Number of distinct sources offering the model.</summary>
    public int SellerCount => Offers.Count;

    public decimal? LowestPrice => Offers.Where(o => o.Price is not null).Select(o => o.Price).Min();

    public bool HasOffers => Offers.Count > 0;

    public Money? Msrp_ =>
        Msrp is { } m && !string.IsNullOrWhiteSpace(MsrpCurrency) ? new Money(m, MsrpCurrency!) : null;
}
