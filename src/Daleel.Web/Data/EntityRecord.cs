using Daleel.Core.Models;

namespace Daleel.Web.Data;

/// <summary>
/// The thin PostgreSQL <em>index</em> row for a search-surfaced entity (product / service / place).
/// Per the storage design Postgres holds the relational graph + lookup indexes while the rich,
/// schema-less content lives as a JSON <c>EntityDocument</c> in R2 (the <c>daleel-data</c> bucket).
/// This row therefore carries only IDs, relations (FKs), minimal lookup metadata, and a pointer to
/// the R2 document — never the bulky specs/offers/description themselves.
/// </summary>
/// <remarks>
/// The same relation IDs stored here are also embedded inside the R2 document, so each side is
/// usable on its own: Postgres for "find/traverse" (which models belong to this brand, which entities
/// came from this search), R2 for "read the full content". Relations use <c>SetNull</c> on delete so
/// pruning a brand/store leaves the index row (and its R2 document) intact but unlinked.
/// </remarks>
public sealed class EntityRecord
{
    /// <summary>Primary key: the entity's stable id (<see cref="StableId.ForEntity"/>), e.g. "p_…"/"sv_…"/"pl_…".</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>"Product" / "Service" / "Place" — the classified intent (stored as text for portability).</summary>
    public string Intent { get; set; } = nameof(SearchIntentType.Product);

    public string Name { get; set; } = string.Empty;

    /// <summary>Normalized name for case/whitespace-insensitive lookup.</summary>
    public string NameKey { get; set; } = string.Empty;

    /// <summary>Market key, e.g. "jordan".</summary>
    public string? Geo { get; set; }

    // ── Relations / embedded reference keys (the graph Postgres exists to traverse) ──
    /// <summary>The search run (SearchJob id) that produced this entity.</summary>
    public string? SearchId { get; set; }

    /// <summary>FK to the owning <see cref="Brand"/> row when resolvable; null otherwise.</summary>
    public int? BrandId { get; set; }
    public Brand? Brand { get; set; }

    /// <summary>FK to the <see cref="Store"/> row when this entity is/belongs to a known store/venue; null otherwise.</summary>
    public int? StoreId { get; set; }
    public Store? Store { get; set; }

    /// <summary>Normalized brand+model key tying this entity to its product profile / scraped prices. Null for services/places.</summary>
    public string? ProductKey { get; set; }

    /// <summary>Parent entity's key when this is a variant/sub-item; usually null.</summary>
    public string? ParentProductKey { get; set; }

    // ── Pointer to the R2 source-of-truth document ──────────────────────────────
    /// <summary>R2 object key of the JSON document (<c>entities/{intent}/{id}.json</c>).</summary>
    public string R2Key { get; set; } = string.Empty;

    /// <summary>Hosted/presigned R2 URL of the document, when the upload succeeded.</summary>
    public string? R2Url { get; set; }

    public DateTimeOffset LastRefreshed { get; set; }

    /// <summary>Normalizes a name into its lookup key (delegates to the shared identity normalization).</summary>
    public static string Normalize(string? value) => StableId.NormalizeIdentity(value);
}
