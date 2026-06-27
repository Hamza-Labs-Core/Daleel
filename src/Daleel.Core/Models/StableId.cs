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
    public static string ForProduct(string? brand, string? model, string? name = null)
    {
        var identity = string.Join('|', new[] { brand, model }
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!.Trim()));
        if (identity.Length == 0)
        {
            identity = name?.Trim() ?? string.Empty;
        }

        return Hash("p", identity);
    }

    /// <summary>Stable id for a brand, keyed on its name (used when no database id exists yet).</summary>
    public static string ForBrand(string? name) => Hash("b", name);

    /// <summary>Stable id for a store, keyed on its name (used when no database id exists yet).</summary>
    public static string ForStore(string? name) => Hash("s", name);

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
