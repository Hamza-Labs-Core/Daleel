using System.Security.Cryptography;
using System.Text;

namespace Daleel.Core.Models;

/// <summary>
/// Deterministic, URL-safe identifiers for the things the UI links to (products, brands, stores).
/// Search results are live projections that mostly aren't persisted with a database key, so routing
/// on the raw display name is fragile: it breaks on URL-unsafe characters, collides when two
/// products share a name, and changes whenever the name is reworded. A stable id hashes the entity's
/// <em>identity</em> instead, so the same product/brand/store always produces the same id — a shared
/// <c>/product/{id}</c> link keeps working even when nothing was saved to the database.
/// </summary>
/// <remarks>
/// Persisted entities (a saved <c>Brand</c>/<c>Store</c> row) carry a real integer database id; those
/// are used verbatim (see the projection's <c>Id</c>) and a detail page recognises them by being
/// numeric. The hashes here are the fallback for everything not yet in the database.
/// </remarks>
public static class StableId
{
    /// <summary>Stable id for a product model, keyed on its brand + model identity (its name as a
    /// last resort), per the requirement to hash from (brand + model name).</summary>
    /// <remarks>
    /// The identity basis is built and normalized identically to <c>ProductProfile.KeyFor</c> (which
    /// defers to <see cref="NormalizeIdentity"/>), so a product *routed* by this id maps to the same
    /// <c>ProductKey</c> it was *persisted* under — otherwise a name with punctuation would route to one
    /// id but be stored under a different key, and the detail page could never find its own saved data.
    /// </remarks>
    public static string ForProduct(string? brand, string? model, string? name = null)
    {
        var basis = !string.IsNullOrWhiteSpace(brand) || !string.IsNullOrWhiteSpace(model)
            ? $"{brand} {model}"
            : name;
        return Hash("p", NormalizeIdentity(basis));
    }

    /// <summary>
    /// The shared identity normalization for products: keep letters/digits/spaces, collapse whitespace
    /// runs, lower-case. <c>ProductProfile.Normalize</c> delegates here so the routing id and the stored
    /// upsert key are always derived from the same basis.
    /// </summary>
    public static string NormalizeIdentity(string? value)
    {
        var filtered = new string((value ?? string.Empty)
            .Where(c => char.IsLetterOrDigit(c) || char.IsWhiteSpace(c)).ToArray());
        return string.Join(' ', filtered.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToLowerInvariant();
    }

    /// <summary>Stable id for a brand, keyed on its name (used when no database id exists yet).</summary>
    public static string ForBrand(string? name) => Hash("b", name);

    /// <summary>Stable id for a store, keyed on its name (used when no database id exists yet).</summary>
    public static string ForStore(string? name) => Hash("s", name);

    /// <summary>Stable id for a hireable service provider, keyed on its name.</summary>
    public static string ForService(string? name) => Hash("sv", NormalizeIdentity(name));

    /// <summary>Stable id for a physical place/venue, keyed on its name.</summary>
    public static string ForPlace(string? name) => Hash("pl", NormalizeIdentity(name));

    /// <summary>
    /// Stable id for any intent-classified entity surfaced by a search. Products keep the existing
    /// <see cref="ForProduct"/> identity (brand+model) so they route/persist consistently; services and
    /// places hash their name. This is the id an <c>EntityDocument</c> (and its Postgres index row) carry.
    /// </summary>
    public static string ForEntity(SearchIntentType intent, string? brand, string? model, string? name) =>
        intent switch
        {
            SearchIntentType.Service => ForService(name),
            SearchIntentType.Place => ForPlace(name),
            _ => ForProduct(brand, model, name)
        };

    /// <summary>
    /// The save-time CONVERGENCE key for a product entity — deliberately distinct from the routing
    /// id (<see cref="ForProduct"/>), which must stay stable for shared /product/{id} links. Two
    /// listings of the same physical product should produce the same identity key even when their
    /// names differ; strongest available evidence wins:
    /// <list type="number">
    ///   <item><b>sku:</b> brand + separator-squashed model — "TAC-24CHSD/TPH11I", "TAC 24CHSD
    ///   TPH11I" and "tac24chsdtph11i" all collide.</item>
    ///   <item><b>fp:</b> geo-scoped name fingerprint — normalized, marketing/stopword tokens
    ///   dropped, remaining tokens SORTED (order-insensitive), brand prepended when known. A
    ///   name-only identity is only trustworthy within one market.</item>
    /// </list>
    /// What this deliberately does NOT catch: cross-language duplicates and model-string vs
    /// marketing-name variants. Those need judgment (vision/LLM), not hashing — a too-aggressive
    /// key would merge DIFFERENT products, and a wrong merge is worse than a duplicate.
    /// </summary>
    public static string IdentityKeyFor(string? geo, string? brand, string? model, string? name)
    {
        var brandKey = NormalizeIdentity(brand);
        if (!string.IsNullOrWhiteSpace(model))
        {
            // Squash separators INSIDE the model string so vendor punctuation variants collide.
            var modelKey = new string(model.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
            if (modelKey.Length > 0)
            {
                return $"sku:{brandKey}:{modelKey}";
            }
        }

        var tokens = NormalizeIdentity(name)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => !FingerprintStopwords.Contains(t))
            .Concat(brandKey.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Distinct()
            .OrderBy(t => t, StringComparer.Ordinal);
        var geoKey = NormalizeIdentity(geo);
        return $"fp:{geoKey}:{string.Join(' ', tokens)}";
    }

    /// <summary>Marketing filler that varies per store without changing what the product IS.</summary>
    private static readonly HashSet<string> FingerprintStopwords = new(StringComparer.Ordinal)
    {
        "original", "new", "offer", "sale", "best", "price", "free", "delivery", "shipping",
        "official", "genuine", "hot", "2024", "2025", "2026",
        "أصلي", "جديد", "عرض", "خصم", "تخفيض", "مجاني", "توصيل", "أفضل", "سعر"
    };

    /// <summary>
    /// Lower-cased, trimmed value hashed to 8 bytes of SHA-256 (16 hex chars) and prefixed with the
    /// entity kind. 64 bits is far more than enough to avoid collisions within a single result set,
    /// while staying short enough to read in a URL.
    /// </summary>
    private static string Hash(string prefix, string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        var hex = Convert.ToHexString(bytes, 0, 8).ToLowerInvariant();
        return $"{prefix}_{hex}";
    }
}
