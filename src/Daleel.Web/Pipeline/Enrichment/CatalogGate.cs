namespace Daleel.Web.Pipeline.Enrichment;

/// <summary>
/// Why one discovered store's catalogue attempt did — or didn't — contribute grid items. Surfaced
/// per store to the admin timeline so an operator can see WHICH gate collapsed a search to a single
/// store, instead of an unexplained "only taghareedstore produced items". Ordered by where the store
/// dropped out of the pipeline.
/// </summary>
public enum CatalogGate
{
    /// <summary>The store's catalogue produced at least one NEW grid item.</summary>
    ProducedItems,

    /// <summary>The store domain did not resolve (DNS / SSRF guard) — a fabricated or dead host, never crawled.</summary>
    Unresolvable,

    /// <summary>Nothing to search the store for: no significant query tokens and every model already complete.</summary>
    NoQueryGap,

    /// <summary>Crawled/rendered but yielded nothing extractable — the empty-render / no-match gate (prime suspect).</summary>
    EmptyCrawl,

    /// <summary>The catalogue matched existing items (price/image/specs) but named no NEW grid item.</summary>
    MatchedNoNewItems,
}

/// <summary>The observable signals of one store's catalogue attempt — the pure inputs the gate is derived from.</summary>
public readonly record struct CatalogAttemptSignals(
    bool ResolvableDomain,
    bool HasQueryGap,
    int VendorPoolCount,
    int BrowserPriceCount,
    int LlmSeedCount,
    int ItemsCreated);

/// <summary>Pure mapping from a store's crawl signals to the gate it hit. No I/O — trivially testable.</summary>
public static class CatalogGateClassifier
{
    public static CatalogGate Classify(CatalogAttemptSignals s)
    {
        // Ordered by where the store dropped out — the earliest gate wins.
        if (!s.ResolvableDomain)
        {
            return CatalogGate.Unresolvable;
        }

        if (!s.HasQueryGap)
        {
            return CatalogGate.NoQueryGap;
        }

        if (s.ItemsCreated > 0)
        {
            return CatalogGate.ProducedItems;
        }

        // Nothing created: an empty crawl (rendered/returned nothing) reads differently from a
        // catalogue that returned products which only matched existing items.
        return s.VendorPoolCount == 0 && s.BrowserPriceCount == 0 && s.LlmSeedCount == 0
            ? CatalogGate.EmptyCrawl
            : CatalogGate.MatchedNoNewItems;
    }
}
