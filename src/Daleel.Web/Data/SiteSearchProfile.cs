namespace Daleel.Web.Data;

/// <summary>
/// What the pipeline has LEARNED about one store site's search interface — the per-domain knowledge
/// that replaces the old hardcoded Shopify-only <c>/search?q=</c> guess (which made taghareedstore,
/// the one Shopify store, the only store that ever yielded items — see the gate audit). Written by
/// the harvest path the first time a candidate search URL actually produces extractable products;
/// read on every later harvest so the winning convention is tried first. Upsert-keyed by
/// <see cref="Domain"/>, same persistence shape as <see cref="Store"/>.
/// </summary>
public sealed class SiteSearchProfile
{
    public int Id { get; set; }

    /// <summary>Bare registrable host, lower-case, no scheme/www — the unique upsert/lookup key.</summary>
    public string Domain { get; set; } = string.Empty;

    /// <summary>The winning search URL with a <c>{query}</c> placeholder, e.g. <c>https://x.jo/?s={query}</c>.</summary>
    public string SearchUrlTemplate { get; set; } = string.Empty;

    /// <summary>How the template was learned: "probe" (a known convention worked) — later "form"/"llm".</summary>
    public string DiscoveredVia { get; set; } = "probe";

    /// <summary>When a harvest last confirmed the template yields extractable products.</summary>
    public DateTimeOffset LastSuccessAt { get; set; }

    /// <summary>Consecutive harvests where the learned template produced nothing — relearn latch.</summary>
    public int ConsecutiveFailures { get; set; }
}
