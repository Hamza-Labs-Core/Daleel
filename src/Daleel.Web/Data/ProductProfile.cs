namespace Daleel.Web.Data;

/// <summary>
/// A persisted, periodically-refreshed product/model deep-dive — the item-side counterpart to
/// <see cref="Brand"/> and <see cref="Store"/>. Built once by the per-item deep-dive (scraping the
/// model's offer page via Context.dev) and reused by every later search that surfaces the same model,
/// so a deep-dive is paid for once rather than on every search.
/// </summary>
/// <remarks>
/// Same persistence shape as <see cref="Store"/>: upsert-keyed by <see cref="NameKey"/> (normalized
/// brand+model), and <see cref="LastRefreshed"/> stored as Unix-ms so the staleness sweep can filter
/// on it in SQLite.
/// </remarks>
public sealed class ProductProfile
{
    public int Id { get; set; }

    /// <summary>Display name as surfaced, e.g. "Samsung AR24 Wind-Free".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Normalized (brand+model, trimmed, lower-cased) — the unique upsert/lookup key.</summary>
    public string NameKey { get; set; } = string.Empty;

    public string? Brand { get; set; }
    public string? Model { get; set; }

    /// <summary>The scraped detail (markdown specs/description) distilled from the offer page.</summary>
    public string? Details { get; set; }

    /// <summary>The page the details were scraped from.</summary>
    public string? SourceUrl { get; set; }

    public DateTimeOffset LastRefreshed { get; set; }

    /// <summary>
    /// Builds the normalized upsert key from a model's brand + model (falling back to its name),
    /// so the same product researched from two different searches maps to one row. Returns "" when
    /// there's nothing usable to key on.
    /// </summary>
    public static string KeyFor(string? brand, string? model, string name)
    {
        var basis = !string.IsNullOrWhiteSpace(brand) || !string.IsNullOrWhiteSpace(model)
            ? $"{brand} {model}"
            : name;
        return Normalize(basis);
    }

    public static string Normalize(string value)
    {
        // Keep letters/digits/spaces, then collapse runs of whitespace so "Samsung  AR24" and
        // "Samsung AR24" key identically regardless of stray spacing in brand/model.
        var filtered = new string((value ?? string.Empty)
            .Where(c => char.IsLetterOrDigit(c) || char.IsWhiteSpace(c)).ToArray());
        return string.Join(' ', filtered.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToLowerInvariant();
    }

    public bool IsStale(DateTimeOffset now, TimeSpan ttl) => now - LastRefreshed > ttl;
}
