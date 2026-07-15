using Daleel.Core.Intelligence;
using Daleel.Core.Models;

namespace Daleel.Web.Services;

/// <summary>One renderable filter facet: the dimension plus its selectable options.</summary>
public sealed record FacetView(string Key, string Label, string? Unit, IReadOnlyList<string> Options);

/// <summary>
/// Builds the grid's product-type filter facets from the search object. Dimensions come from the
/// planner (<see cref="SearchStrategy.Facets"/>) — or, when the planner named none, from the
/// existing LLM category schema (<see cref="ProductSchema.Fields"/>, Key-importance first), so
/// per-type filters appear even for strategies that predate the facet fields. Options are the
/// union of planner candidate values and the values present in the results' specs. Facets are
/// ALWAYS emitted, never hidden for sparseness (an empty facet renders disabled, not absent).
/// Spec keys bind loosely — "screen size" matches "screen_size" and "Screen Size" — because the
/// planner, the schema, and per-store extraction each case/format keys their own way.
/// </summary>
public static class FacetBuilder
{
    /// <summary>
    /// Filter dimensions the grid ALWAYS renders as its generic controls (Brand/Source/Condition
    /// selects, the price fields, the sort). A planner-named facet on one of these would put the
    /// same control on screen twice (QA: a "Brand" and a "Condition" facet rendered beside the
    /// built-in Brand and Condition filters), so they are dropped here. Keys compare loosely.
    /// </summary>
    private static readonly HashSet<string> BuiltInFilterKeys = new(StringComparer.Ordinal)
    {
        "brand", "condition", "source", "price", "sort", "sortby", "minprice", "maxprice"
    };

    /// <summary>The renderable facets for this search: every named dimension with its option union.</summary>
    public static IReadOnlyList<FacetView> Build(
        SearchStrategy? strategy, ProductSchema schema, IReadOnlyList<ProductModel> models)
    {
        var dimensions = (strategy?.Facets is { Count: > 0 } named ? named : SchemaFallback(schema))
            .Where(f => !DuplicatesBuiltInFilter(f.Key))
            .ToList();
        if (dimensions.Count == 0)
        {
            return Array.Empty<FacetView>();
        }

        return dimensions.Select(f =>
        {
            var options = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var v in f.Values)
            {
                var t = CanonicalValue(v, f.Unit);
                if (t.Length > 0 && seen.Add(t))
                {
                    options.Add(t);
                }
            }
            foreach (var m in models)
            {
                if (SpecValue(m, f.Key) is { } v &&
                    CanonicalValue(v, f.Unit) is { Length: > 0 } cv && seen.Add(cv))
                {
                    options.Add(cv);
                }
            }
            return new FacetView(f.Key, f.Label, f.Unit, options);
        }).ToList();
    }

    /// <summary>
    /// True when the model's spec for the facet key (loose match) equals the selected value.
    /// When the facet declares a <paramref name="unit"/>, both sides are canonicalized first, so a
    /// spec of "9 Kg" matches the option "9" on a facet whose unit is "kg".
    /// </summary>
    public static bool Matches(ProductModel model, string facetKey, string value, string? unit = null) =>
        SpecValue(model, facetKey) is { } v &&
        string.Equals(CanonicalValue(v, unit), CanonicalValue(value, unit), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Strips the facet's declared unit off the end of a value ("9 Kg" → "9", "9KG" → "9" for unit
    /// "kg") so planner candidates and per-store spec values collapse to one option instead of the
    /// fragmented pair QA showed. Values in OTHER notations (e.g. an Arabic unit word) simply keep
    /// their own spelling — fragmentation there is accepted rather than guessed at.
    /// </summary>
    private static string CanonicalValue(string value, string? unit)
    {
        var t = value.Trim();
        if (unit is { Length: > 0 } && t.Length > unit.Length &&
            t.EndsWith(unit, StringComparison.OrdinalIgnoreCase))
        {
            t = t[..^unit.Length].TrimEnd();
        }
        return t;
    }

    /// <summary>Loosely compares a facet key against the grid's built-in filter dimensions.</summary>
    private static bool DuplicatesBuiltInFilter(string facetKey)
    {
        var key = NormalizeKey(facetKey);
        if (BuiltInFilterKeys.Contains(key))
        {
            return true;
        }

        // "Price (JOD)" normalizes to "pricejod" — a bare price facet with a short currency/unit
        // suffix still duplicates the built-in price fields. A real derived metric like
        // "price per diaper" keeps a long remainder and stays a legitimate facet.
        return key.StartsWith("price", StringComparison.Ordinal) && key.Length - "price".Length <= 5;
    }

    /// <summary>The model's trimmed spec value under the loose-normalized facet key, else null.</summary>
    private static string? SpecValue(ProductModel model, string facetKey)
    {
        var wanted = NormalizeKey(facetKey);
        foreach (var (k, v) in model.Specs)
        {
            if (NormalizeKey(k) == wanted && !string.IsNullOrWhiteSpace(v))
            {
                return v.Trim();
            }
        }
        return null;
    }

    /// <summary>Schema fields as facet dimensions, Key-importance first (the defining specs lead).</summary>
    private static IReadOnlyList<SearchFacet> SchemaFallback(ProductSchema schema) =>
        schema.Fields
            .OrderBy(f => f.Importance == SpecImportance.Key ? 0 : 1)
            .Select(f => new SearchFacet { Key = f.Key, Label = f.Label, Unit = f.Unit })
            .ToList();

    /// <summary>Case-fold and strip separators so "Screen Size" ≡ "screen_size" ≡ "screen-size".
    /// Internal (not private) because the grid's facet PRE-SELECTION matches the query's stated
    /// Specs keys against facet keys, and it must use this exact same loose normalization.</summary>
    internal static string NormalizeKey(string key) =>
        new(key.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
