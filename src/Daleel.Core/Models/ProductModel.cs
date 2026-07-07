using System;
using System.Linq;
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

    /// <summary>
    /// The selling store's own site/page (root or store profile), distinct from <see cref="Url"/>
    /// (the product page). Lets the card's seller chip link to the store itself.
    /// </summary>
    public string? StoreUrl { get; init; }

    /// <summary>Whether the offer advertises free shipping.</summary>
    public bool FreeShipping { get; init; }

    /// <summary>Set by the aggregator: this is the cheapest offer for the model.</summary>
    public bool IsLowest { get; init; }

    /// <summary>
    /// True when the price is INDICATIVE — parsed loosely from a rendered page's text or a search
    /// snippet rather than structured listing data. A potential price worth checking, not a quote:
    /// the UI renders it with an ≈ affordance ("verify at the store") that leads to the store, while
    /// exact prices render as firm figures leading to the listing.
    /// </summary>
    public bool IsIndicative { get; init; }

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
            if (IsIndicative) tags.Add("UNVERIFIED");
            return tags;
        }
    }
}

/// <summary>A review/discussion source that names a specific model (attached to its card).</summary>
public sealed record ItemReview(string? Title, string Url, string? Snippet = null, string? Source = null);

/// <summary>Any other link that mentions a specific model — article, comparison, source page.</summary>
public sealed record ItemLink(string Title, string Url, string? Source = null);

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
    /// <summary>The primary/first photo (kept for callers that want a single image; enrichment fills it).</summary>
    public string? ImageUrl { get; init; }

    /// <summary>
    /// EVERY photo found for this item — aggregated across ALL the listings/offers that matched it. Raw,
    /// unscreened, distinct. The full candidate gallery is <see cref="CandidateImages"/> (these plus
    /// <see cref="ImageUrl"/>); the user sees the VERIFIED subset (<see cref="DisplayImages"/>).
    /// </summary>
    public IReadOnlyList<string> Images { get; init; } = Array.Empty<string>();

    /// <summary>
    /// The subset of <see cref="CandidateImages"/> the halal vision screen VERIFIED clean. The UI renders
    /// ONLY these (fail-closed): a photo is hidden until promoted here, and re-hides if it leaves the
    /// candidate set. Preserving the raw candidates lets an admin whitelist / a retry un-hide one later.
    /// </summary>
    public IReadOnlyList<string> VerifiedImages { get; init; } = Array.Empty<string>();

    /// <summary>Every distinct candidate photo, primary first — what the screen must clear before any renders.</summary>
    public IReadOnlyList<string> CandidateImages =>
        (string.IsNullOrWhiteSpace(ImageUrl) ? Images : Images.Prepend(ImageUrl!))
            .Where(IsUsableImageUrl)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// A usable product image is an absolute http(s) URL. Extractors occasionally emit junk in the image
    /// field — the literal string "null"/"undefined", a bare word, or a relative path — which passed the
    /// old "not whitespace" check and then, because the vision screen skips non-http urls, got promoted as
    /// a "verified" image (rendering &lt;img src="null"&gt; → 404) and logged as a shown image. Requiring a
    /// real http(s) url at the candidate gate keeps that junk out of screening, display, and the audit.
    /// </summary>
    private static bool IsUsableImageUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>The photos to actually render — verified AND still a candidate, in order. A fail-closed gallery.</summary>
    public IReadOnlyList<string> DisplayImages
    {
        get
        {
            var verified = new HashSet<string>(VerifiedImages, StringComparer.Ordinal);
            return CandidateImages.Where(verified.Contains).ToList();
        }
    }

    /// <summary>The primary photo to render (first verified) — for single-image surfaces like the grid card.</summary>
    public string? DisplayImageUrl => DisplayImages.Count > 0 ? DisplayImages[0] : null;

    /// <summary>
    /// Stable, URL-safe identifier used to route to the model's detail page. Products aren't persisted
    /// with a database key, so this is a deterministic hash of the model's (brand + model) identity —
    /// see <see cref="StableId.ForProduct"/>. The human <see cref="Name"/> is kept as a display/scan
    /// fallback and travels alongside the id as a query parameter.
    /// </summary>
    public string Id => StableId.ForProduct(Brand, Model, Name);

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

    /// <summary>Aggregated buyer rating (1–5) across the model's listings, when any source carried one.</summary>
    public double? Rating { get; init; }

    /// <summary>Total review count behind <see cref="Rating"/>, when the sources reported it.</summary>
    public int? RatingCount { get; init; }

    /// <summary>Every place the model is available, sorted cheapest-first.</summary>
    public IReadOnlyList<PriceOffer> Offers { get; init; } = Array.Empty<PriceOffer>();

    /// <summary>Reviews/discussions that name THIS model — card-attached, never an orphan section.</summary>
    public IReadOnlyList<ItemReview> Reviews { get; init; } = Array.Empty<ItemReview>();

    /// <summary>Other links mentioning THIS model (articles, comparisons, sources).</summary>
    public IReadOnlyList<ItemLink> Mentions { get; init; } = Array.Empty<ItemLink>();

    /// <summary>This model's page on its brand's site, when found (regional preferred).</summary>
    public string? BrandSiteUrl { get; init; }

    /// <summary>The brand's regional site root for the market (e.g. samsung.com/jo), when known.</summary>
    public string? BrandRegionalUrl { get; init; }

    /// <summary>
    /// The offer the card leads with: the cheapest EXACT price first, then the cheapest indicative
    /// ("potential") price, then any offer at all — a verified figure always beats an approximation.
    /// </summary>
    public PriceOffer? LowestOffer =>
        Offers.FirstOrDefault(o => o.Price is not null && !o.IsIndicative)
        ?? Offers.FirstOrDefault(o => o.Price is not null)
        ?? Offers.FirstOrDefault();

    /// <summary>Number of distinct sources offering the model.</summary>
    public int SellerCount => Offers.Count;

    public decimal? LowestPrice => Offers.Where(o => o.Price is not null).Select(o => o.Price).Min();

    /// <summary>
    /// The price the UI leads with — <see cref="LowestOffer"/>'s (exact preferred over indicative).
    /// Every user-facing surface (card, sort, filters, detail, compare) must key off THIS, never
    /// <see cref="LowestPrice"/>: mixing the two shows one number and sorts/links by another.
    /// </summary>
    public decimal? DisplayPrice => LowestOffer?.Price;

    /// <summary>True when the only prices known for this model are indicative (loosely parsed).</summary>
    public bool LowestIsIndicative => LowestOffer is { Price: not null, IsIndicative: true };

    public bool HasOffers => Offers.Count > 0;

    public Money? Msrp_ =>
        Msrp is { } m && !string.IsNullOrWhiteSpace(MsrpCurrency) ? new Money(m, MsrpCurrency!) : null;
}
