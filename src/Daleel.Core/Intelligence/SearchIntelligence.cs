namespace Daleel.Core.Intelligence;

/// <summary>
/// The LLM's up-front "thinking" about a product category, produced once at the start of a
/// product search and threaded through the rest of the pipeline so every later step knows what
/// it is looking for. Before gathering sources the agent asks: for <em>this</em> category, which
/// kinds of store are relevant (electronics shops, not groceries)? Which brands compete here?
/// Which specs decide a purchase? Do product images matter? The answers shape source gathering,
/// extraction (via the <see cref="Schema"/>), profile relevance, and the compare table.
/// </summary>
public record SearchIntelligence
{
    /// <summary>The category as understood from the query, e.g. "air conditioner".</summary>
    public string Category { get; init; } = string.Empty;

    /// <summary>The product type, mirrored onto <see cref="Schema"/>; "general" when unknown.</summary>
    public string ProductType { get; init; } = "general";

    /// <summary>
    /// The kinds of store that actually sell this category, e.g. "electronics store",
    /// "HVAC retailer", "appliance store". Guides which stores are worth enriching.
    /// </summary>
    public IReadOnlyList<string> RelevantStoreTypes { get; init; } = Array.Empty<string>();

    /// <summary>Brands known to compete in this category/market, budget through premium.</summary>
    public IReadOnlyList<string> ExpectedBrands { get; init; } = Array.Empty<string>();

    /// <summary>The comparison schema for this product type — the specs that matter.</summary>
    public ProductSchema Schema { get; init; } = ProductSchema.General;

    /// <summary>A human, market-aware price expectation, e.g. "typically 300–1,200 JOD".</summary>
    public string? PriceExpectation { get; init; }

    /// <summary>Whether product images are important for this category (true for most goods).</summary>
    public bool ImagesMatter { get; init; } = true;

    /// <summary>The LLM's one-line rationale, surfaced in diagnostics/progress.</summary>
    public string? Reasoning { get; init; }

    /// <summary>True when the intelligence is effectively empty (no schema, brands or store types).</summary>
    public bool IsEmpty =>
        Schema.IsEmpty && ExpectedBrands.Count == 0 && RelevantStoreTypes.Count == 0;

    /// <summary>Neutral intelligence for a category, used when the LLM call fails or is skipped.</summary>
    public static SearchIntelligence Neutral(string category) => new() { Category = category };
}
