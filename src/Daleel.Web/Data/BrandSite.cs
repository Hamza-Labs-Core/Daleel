namespace Daleel.Web.Data;

/// <summary>
/// One level of a brand's site hierarchy — the "brand global site, regional site, local site" model.
/// Brand research discovers up to one row per (level, market): the GLOBAL site (also mirrored on
/// <see cref="Brand.Website"/> for back-compat), an optional REGIONAL variant (a neighbouring-market
/// ccTLD, a /mea-style section, or the Arabic edition of the global site), and the market-LOCAL
/// storefront per country. Each recorded site gets its own catalogue harvest so a shopper sees the
/// models and prices listed for THEIR market, with the global catalogue filling spec/image gaps.
/// </summary>
/// <remarks>
/// Upsert-keyed by (<see cref="BrandId"/>, <see cref="Level"/>, <see cref="CountryCode"/>) so
/// re-discovering a level updates its row rather than duplicating it. <see cref="CountryCode"/> is
/// the market's ISO alpha-2 code for local rows, a region hint ("mea", a neighbour's cc, "ar") for
/// regional ones, and null for the single global row. <see cref="LastRefreshed"/> persists as
/// Unix-ms bigint like the other profile timestamps.
/// </remarks>
public sealed class BrandSite
{
    public int Id { get; set; }

    /// <summary>Owning brand (FK). Sites are deleted with their brand.</summary>
    public int BrandId { get; set; }

    /// <summary>Navigation to the owning <see cref="Brand"/>.</summary>
    public Brand? Brand { get; set; }

    /// <summary>Hierarchy level — one of the <see cref="BrandSiteLevel"/> constants.</summary>
    public string Level { get; set; } = BrandSiteLevel.Global;

    /// <summary>
    /// Market scope: the ISO alpha-2 country code for local sites ("jo"), a region hint for
    /// regional ones ("mea", "ae", "ar"), null for the global site.
    /// </summary>
    public string? CountryCode { get; set; }

    /// <summary>The site URL as discovered.</summary>
    public string Url { get; set; } = string.Empty;

    public DateTimeOffset LastRefreshed { get; set; }

    /// <summary>True when this discovery is older than <paramref name="ttl"/> and worth re-searching.</summary>
    public bool IsStale(DateTimeOffset now, TimeSpan ttl) => now - LastRefreshed > ttl;
}

/// <summary>The three site-hierarchy levels brand research distinguishes.</summary>
public static class BrandSiteLevel
{
    /// <summary>The brand's worldwide site (e.g. samsung.com) — the reference catalogue.</summary>
    public const string Global = "global";

    /// <summary>A multi-market regional variant (e.g. a /mea section or a neighbouring ccTLD).</summary>
    public const string Regional = "regional";

    /// <summary>The market-local storefront (e.g. samsung.com/jo or acme.jo) — in-market prices.</summary>
    public const string Local = "local";
}
