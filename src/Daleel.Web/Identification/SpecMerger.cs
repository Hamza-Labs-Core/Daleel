using System.Text.RegularExpressions;
using Daleel.Core.Intelligence;

namespace Daleel.Web.Identification;

/// <summary>
/// One source of specs feeding the merge, tagged with how authoritative it is. Higher
/// <see cref="Priority"/> wins a conflict — the brand's own site beats a store listing, which beats a
/// review aggregator.
/// </summary>
public sealed record SpecSource(string Name, int Priority, IReadOnlyDictionary<string, string> Specs)
{
    /// <summary>The brand's official site — the canonical authority for specs.</summary>
    public const int BrandPriority = 100;

    /// <summary>A store/marketplace product listing.</summary>
    public const int StorePriority = 50;

    /// <summary>A review or third-party aggregator — useful but least trusted.</summary>
    public const int ReviewPriority = 10;

    public static SpecSource Brand(IReadOnlyDictionary<string, string> specs, string name = "brand-site") =>
        new(name, BrandPriority, specs);

    public static SpecSource Store(IReadOnlyDictionary<string, string> specs, string name = "store-listing") =>
        new(name, StorePriority, specs);

    public static SpecSource Review(IReadOnlyDictionary<string, string> specs, string name = "review") =>
        new(name, ReviewPriority, specs);
}

/// <summary>
/// Merges specs gathered from several sources into one canonical, de-duplicated, unit-normalized sheet:
/// the same attribute quoted by two sources collapses to a single value (the most authoritative one),
/// units are reconciled (<see cref="UnitNormalizer"/>), and — when a category <see cref="ProductSchema"/>
/// is known — the output is keyed and ordered by the schema's fields so the UI shows the <em>right</em>
/// attributes first instead of a blind dump.
/// </summary>
public interface ISpecMerger
{
    /// <summary>
    /// Produces the canonical spec sheet from <paramref name="sources"/>. When <paramref name="schema"/>
    /// is supplied and non-empty, recognized fields are renamed to the schema's machine keys and ordered
    /// schema-first; unrecognized specs follow, alphabetically.
    /// </summary>
    IReadOnlyDictionary<string, string> Merge(IReadOnlyList<SpecSource> sources, ProductSchema? schema = null);
}

public sealed class SpecMerger : ISpecMerger
{
    public IReadOnlyDictionary<string, string> Merge(IReadOnlyList<SpecSource> sources, ProductSchema? schema = null)
    {
        // Gather every (canonical key → best candidate) across sources. A candidate carries its source
        // priority so the highest-authority value wins a conflict; ties keep the first-seen value.
        var best = new Dictionary<string, (int Priority, string Value)>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in sources.OrderByDescending(s => s.Priority))
        {
            foreach (var (rawKey, rawValue) in source.Specs)
            {
                var key = NormalizeKey(rawKey);
                var value = UnitNormalizer.Normalize(rawValue);
                if (key.Length == 0 || value.Length == 0)
                {
                    continue;
                }

                if (!best.TryGetValue(key, out var current) || source.Priority > current.Priority)
                {
                    best[key] = (source.Priority, value);
                }
            }
        }

        var merged = best.ToDictionary(kv => kv.Key, kv => kv.Value.Value, StringComparer.OrdinalIgnoreCase);
        return schema is null || schema.IsEmpty ? Order(merged) : ApplySchema(merged, schema);
    }

    /// <summary>Re-keys recognized specs to schema field keys and orders schema-first, extras after.</summary>
    private static IReadOnlyDictionary<string, string> ApplySchema(
        IDictionary<string, string> merged, ProductSchema schema)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var consumed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var field in schema.Fields)
        {
            // A field matches a merged spec by its machine key or by its (normalized) human label.
            var fieldKeys = new[] { NormalizeKey(field.Key), NormalizeKey(field.Label) };
            var match = merged.FirstOrDefault(kv =>
                !consumed.Contains(kv.Key) && fieldKeys.Contains(kv.Key, StringComparer.OrdinalIgnoreCase));

            if (match.Key is not null)
            {
                result[field.Key] = match.Value;
                consumed.Add(match.Key);
            }
        }

        // Append anything the schema didn't claim, alphabetically, so nothing is lost.
        foreach (var kv in merged.Where(kv => !consumed.Contains(kv.Key)).OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            result[kv.Key] = kv.Value;
        }

        return result;
    }

    private static IReadOnlyDictionary<string, string> Order(IDictionary<string, string> merged) =>
        merged.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Normalizes a spec key to lower snake_case so "Screen Size", "screen-size" and "screen_size" all
    /// collapse to one key — the foundation of cross-source de-duplication.
    /// </summary>
    internal static string NormalizeKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return string.Empty;
        }

        var lowered = key.Trim().ToLowerInvariant();
        var snake = Regex.Replace(lowered, @"[^a-z0-9]+", "_");
        return snake.Trim('_');
    }
}
