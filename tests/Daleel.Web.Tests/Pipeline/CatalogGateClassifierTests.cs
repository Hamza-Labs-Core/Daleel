using Daleel.Web.Pipeline.Enrichment;
using FluentAssertions;
using Xunit;

namespace Daleel.Web.Tests.Pipeline;

// The per-store diagnostic that explains WHY a discovered store did or didn't put items on the grid —
// the signal that turns an unexplained "only taghareedstore produced items" into a per-store gate an
// operator can read on the admin timeline. Classification is a pure function of the crawl's own signals.
public class CatalogGateClassifierTests
{
    private static CatalogAttemptSignals Signals(
        bool resolvable = true, bool hasQueryGap = true,
        int vendorPool = 0, int browserPrices = 0, int llmSeed = 0, int itemsCreated = 0) =>
        new(resolvable, hasQueryGap, vendorPool, browserPrices, llmSeed, itemsCreated);

    [Fact]
    public void Unresolvable_domain_is_the_first_gate()
    {
        // A host that fails DNS/SSRF never gets crawled — even if every other signal looks fine.
        CatalogGateClassifier.Classify(Signals(resolvable: false, itemsCreated: 5))
            .Should().Be(CatalogGate.Unresolvable);
    }

    [Fact]
    public void No_query_gap_short_circuits_before_any_crawl()
    {
        CatalogGateClassifier.Classify(Signals(hasQueryGap: false))
            .Should().Be(CatalogGate.NoQueryGap);
    }

    [Fact]
    public void Created_items_means_the_store_produced()
    {
        CatalogGateClassifier.Classify(Signals(vendorPool: 12, itemsCreated: 3))
            .Should().Be(CatalogGate.ProducedItems);
    }

    [Fact]
    public void Empty_crawl_when_nothing_was_extracted()
    {
        // The prime suspect for "only one store": the render/crawl came back with nothing to attach —
        // no vendor catalogue, no browser price lines, no LLM-named products.
        CatalogGateClassifier.Classify(Signals(vendorPool: 0, browserPrices: 0, llmSeed: 0, itemsCreated: 0))
            .Should().Be(CatalogGate.EmptyCrawl);
    }

    [Fact]
    public void Matched_but_created_nothing_new_is_distinct_from_an_empty_crawl()
    {
        // The catalogue DID return products (they attached prices/images to existing models) but named
        // no NEW grid item — a different failure from an empty render, and it must read differently.
        CatalogGateClassifier.Classify(Signals(vendorPool: 8, browserPrices: 2, llmSeed: 0, itemsCreated: 0))
            .Should().Be(CatalogGate.MatchedNoNewItems);
    }
}
