namespace Daleel.Web.Data;

/// <summary>
/// One observation of a product's price at a store, captured by the price-scraping pass. Unlike the
/// upsert-keyed <see cref="Brand"/>/<see cref="Store"/>/<see cref="ProductProfile"/> profiles, this is
/// an append-only time series: prices change, so every scrape writes a fresh row stamped with
/// <see cref="ScrapedAt"/>. The latest row per (product, store) is the current price; the history lets
/// us chart movement and spot deals over time.
/// </summary>
/// <remarks>
/// Keyed for reads by <see cref="ProductKey"/> (the normalized brand+model, shared with
/// <see cref="ProductProfile.KeyFor"/>) so the same model maps to one series regardless of how its
/// display name was spelled. <see cref="ScrapedAt"/> persists as Unix-ms — a stable, provider-agnostic
/// encoding the WHERE clause can still order/filter — the same trick the profiles use for <c>LastRefreshed</c>.
/// </remarks>
public sealed class ScrapedPrice
{
    public long Id { get; set; }

    /// <summary>Display name of the product as scraped, e.g. "Samsung AR24 Wind-Free".</summary>
    public string ProductName { get; set; } = string.Empty;

    /// <summary>Normalized (brand+model) lookup key tying every observation of one model together.</summary>
    public string ProductKey { get; set; } = string.Empty;

    /// <summary>The store/retailer the price was observed at, e.g. "Smart Buy" or a domain.</summary>
    public string StoreName { get; set; } = string.Empty;

    /// <summary>The observed price, or null when only availability (not a number) could be read.</summary>
    public decimal? Price { get; set; }

    /// <summary>ISO currency code or symbol as scraped (e.g. "JOD", "$"), when known.</summary>
    public string? Currency { get; set; }

    /// <summary>The page the price was read from.</summary>
    public string? SourceUrl { get; set; }

    /// <summary>
    /// The product's image URL as scraped, when the source carried one. The LLM site-crawl extracts a
    /// photo per listing; persisting it here is what lets the grid-builder (which rebuilds the live grid
    /// from these rows, NOT from the rich entity documents) show the crawl's own images instead of leaving
    /// every crawled item imageless and falling back to a paid image search.
    /// </summary>
    public string? ImageUrl { get; set; }

    /// <summary>Inventory-monitor presence stamp: when a sync last saw this listing live on the
    /// store. A monitored item missing across a whole sync gets its offer availability flipped —
    /// never deleted.</summary>
    public DateTimeOffset? LastSeenAt { get; set; }

    /// <summary>The store's stock wording as extracted ("in stock", "متوفر", "sold out"), when shown.
    /// Classified for display by <c>StockStatus</c>; free-form here so nothing is lost in transit.</summary>
    public string? Availability { get; set; }

    /// <summary>Which scraper produced this observation ("context.dev" or "cloudflare-browser").</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>UTC instant the observation was captured.</summary>
    public DateTimeOffset ScrapedAt { get; set; }
}
