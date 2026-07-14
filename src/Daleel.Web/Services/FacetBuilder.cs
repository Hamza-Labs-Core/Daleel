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
    /// <summary>The renderable facets for this search: every named dimension with its option union.</summary>
    public static IReadOnlyList<FacetView> Build(
        SearchStrategy? strategy, ProductSchema schema, IReadOnlyList<ProductModel> models)
    {
        var dimensions = strategy?.Facets is { Count: > 0 } named
            ? named
            : SchemaFallback(schema);
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
                var t = v.Trim();
                if (t.Length > 0 && seen.Add(t))
                {
                    options.Add(t);
                }
            }
            foreach (var m in models)
            {
                if (SpecValue(m, f.Key) is { } v && seen.Add(v))
                {
                    options.Add(v);
                }
            }
            return new FacetView(f.Key, f.Label, f.Unit, options);
        }).ToList();
    }

    /// <summary>True when the model's spec for the facet key (loose match) equals the selected value.</summary>
    public static bool Matches(ProductModel model, string facetKey, string value) =>
        SpecValue(model, facetKey) is { } v && string.Equals(v, value.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>The model's trimmed spec value under the loose-normalized facet key, else null.</summary>
    private static string? SpecValue(ProductModel model, string facetKey)
    {
        var wanted = Normalize(facetKey);
        foreach (var (k, v) in model.Specs)
        {
            if (Normalize(k) == wanted && !string.IsNullOrWhiteSpace(v))
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

    /// <summary>Case-fold and strip separators so "Screen Size" ≡ "screen_size" ≡ "screen-size".</summary>
    private static string Normalize(string key) =>
        new(key.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
