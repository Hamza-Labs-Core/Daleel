using Daleel.Core.Models;

namespace Daleel.Core.Persistence;

/// <summary>
/// The self-contained JSON document persisted to R2 (the <c>daleel-data</c> bucket) for one entity a
/// search surfaced — a product, service or place. R2 is the source of truth for the rich content;
/// PostgreSQL holds only a thin index row (see the <c>EntityRecord</c> entity) for relational
/// traversal. Per the storage design the document is intentionally <em>schema-less</em>: the
/// intent-specific attributes (product specs, service tiers, place hours/address/map) live in the
/// free-form <see cref="Specs"/> dictionary and <see cref="Offers"/> list rather than in typed
/// Postgres columns.
/// </summary>
/// <remarks>
/// The document is self-describing: it embeds its own relation IDs (<see cref="BrandId"/>,
/// <see cref="StoreId"/>, <see cref="ParentProductKey"/>, <see cref="SearchId"/>, …) so it can be
/// read and understood on its own, without joining back to Postgres. Those same IDs are mirrored
/// onto the Postgres index row, which is what queries/joins traverse.
/// </remarks>
public sealed record EntityDocument
{
    /// <summary>Document schema version, so a future shape change can be migrated/read defensively.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Stable, URL-safe identity of this entity (see <see cref="StableId.ForEntity"/>). Also the R2 key.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>What KIND of entity this is — drives how the attributes are interpreted.</summary>
    public SearchIntentType Intent { get; init; } = SearchIntentType.Product;

    /// <summary>Display name (product/provider/venue name).</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Manufacturer (products) or parent chain (services/places); null when standalone.</summary>
    public string? Brand { get; init; }

    /// <summary>Model number/name for products; null for services/places.</summary>
    public string? Model { get; init; }

    /// <summary>Primary image/logo/photo URL when known.</summary>
    public string? ImageUrl { get; init; }

    /// <summary>All discovered image/photo URLs for this entity.</summary>
    public IReadOnlyList<string> ImageUrls { get; init; } = Array.Empty<string>();

    // ── Market / origin context ──────────────────────────────────────────────
    /// <summary>Market key the entity was found in, e.g. "jordan".</summary>
    public string? Geo { get; init; }

    /// <summary>Human-readable country, e.g. "Jordan".</summary>
    public string? Country { get; init; }

    /// <summary>The query that surfaced this entity.</summary>
    public string? Query { get; init; }

    // ── Embedded relation IDs (make the document self-contained) ──────────────
    /// <summary>The search run (SearchJob id) that produced this document.</summary>
    public string? SearchId { get; init; }

    /// <summary>Stable brand id (<see cref="StableId.ForBrand"/>) — the brand→entity relation, embedded.</summary>
    public string? BrandId { get; init; }

    /// <summary>Stable store id (<see cref="StableId.ForStore"/>) when this entity IS, or belongs to, a store/venue.</summary>
    public string? StoreId { get; init; }

    /// <summary>
    /// Normalized brand+model key (same basis as <c>ProductProfile.KeyFor</c>) — ties this document to the
    /// product profile / scraped-price rows stored under the same key. Null for services/places.
    /// </summary>
    public string? ProductKey { get; init; }

    /// <summary>Parent product's key/id when this entity is a variant or sub-item of another. Usually null.</summary>
    public string? ParentProductKey { get; init; }

    // ── Flexible, intent-specific content ─────────────────────────────────────
    /// <summary>
    /// Free-form attributes: product specs (btu, ram…), service details (availability, contact, area),
    /// or place details (address, hours, phone, rating, mapUrl). The schema-less heart of the document.
    /// </summary>
    public IReadOnlyDictionary<string, string> Specs { get; init; } =
        new Dictionary<string, string>();

    /// <summary>
    /// Priced offers: product seller offers, or service pricing tiers (tier name in <see cref="EntityOffer.Source"/>).
    /// Normally empty for places.
    /// </summary>
    public IReadOnlyList<EntityOffer> Offers { get; init; } = Array.Empty<EntityOffer>();

    public IReadOnlyList<string> Pros { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Cons { get; init; } = Array.Empty<string>();

    /// <summary>One-line verdict/summary, when distilled from the context.</summary>
    public string? Summary { get; init; }

    /// <summary>When this document was captured.</summary>
    public DateTimeOffset CapturedAt { get; init; }

    /// <summary>
    /// The R2 object key for an entity document: <c>entities/{intent}/{id}.json</c>. Deterministic from the
    /// id, so the same entity always round-trips to the same object (re-runs overwrite in place).
    /// </summary>
    public static string KeyFor(SearchIntentType intent, string id) =>
        $"entities/{intent.ToString().ToLowerInvariant()}/{id}.json";

    /// <summary>This document's R2 object key.</summary>
    public string ObjectKey => KeyFor(Intent, Id);
}

/// <summary>A priced offer (product seller) or pricing tier (service package) within an <see cref="EntityDocument"/>.</summary>
public sealed record EntityOffer
{
    /// <summary>Seller/marketplace name (products) or tier/package name (services).</summary>
    public string Source { get; init; } = string.Empty;
    public decimal? Price { get; init; }
    public string? Currency { get; init; }
    public string? Url { get; init; }

    /// <summary>"new" / "used" / "refurbished" for products; null otherwise.</summary>
    public string? Condition { get; init; }
}
