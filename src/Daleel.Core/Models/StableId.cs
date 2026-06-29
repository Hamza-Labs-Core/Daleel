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
