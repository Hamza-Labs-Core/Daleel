using Daleel.Core.Models;
using Daleel.Web.Pipeline;
using FluentAssertions;
using Xunit;

namespace Daleel.Web.Tests.Pipeline;

/// <summary>
/// Pins the catalogue→model creation rule: a store catalogue is a first-class product source. An
/// entry that matches no existing model but IS what the user searched for becomes a NEW grid model
/// (the jo-cell case: a deep local catalogue used to contribute zero items unless web extraction
/// had already named them).
/// </summary>
public class CatalogDiscoveryTests
{
    private static (string, decimal?, string?, string?, string?, string?, bool) Entry(
        string name, decimal? price = 99m, string? url = "https://jo-cell.com/products/x") =>
        (name, price, "JOD", url, null, null, true);

    private static List<ProductModel> Existing(params string[] names) =>
        names.Select(n => new ProductModel { Name = n }).ToList();

    [Fact]
    public void Relevant_unmatched_entries_become_models_with_the_store_offer()
    {
        var (models, created) = ItemEnrichmentService.AppendCatalogDiscoveries(
            Existing("Krups Precision Espresso Machine"),
            new[]
            {
                Entry("DeLonghi Espresso Maker Dedica Style 15 Bar 1300 Watt"),
                Entry("Moulinex Espresso Machine XP330A10 15 Bar"),
                Entry("Ceramic Tea Cup Set 6pcs") // in the catalogue, not what the user searched
            },
            storeName: "Jo Cell", query: "espresso machines in Jordan");

        created.Should().BeEquivalentTo(new[]
        {
            "DeLonghi Espresso Maker Dedica Style 15 Bar 1300 Watt",
            "Moulinex Espresso Machine XP330A10 15 Bar"
        });
        models.Should().HaveCount(3, "existing model + 2 discoveries; the tea set is irrelevant");
        var delonghi = models.Single(m => m.Name.StartsWith("DeLonghi"));
        var offer = delonghi.Offers.Should().ContainSingle().Subject;
        offer.Price.Should().Be(99m);
        offer.IsIndicative.Should().BeTrue("drained catalogue prices are leads, not live quotes");
        offer.Url.Should().Be("https://jo-cell.com/products/x");
    }

    [Fact]
    public void Entries_matching_an_existing_model_or_each_other_are_not_duplicated()
    {
        var (models, created) = ItemEnrichmentService.AppendCatalogDiscoveries(
            Existing("Moulinex Espresso Machine XP330A10"),
            new[]
            {
                Entry("Moulinex Espresso Machine XP330A10 15 Bar"), // decorates the existing model instead
                Entry("Beko Espresso Coffee Machine 19 Bar"),
                Entry("Beko Espresso Coffee Machine 19 Bar")        // same discovery twice in the catalogue
            },
            storeName: "Jo Cell", query: "espresso machines in Jordan");

        created.Should().ContainSingle().Which.Should().StartWith("Beko");
        models.Should().HaveCount(2);
    }

    [Fact]
    public void Single_shared_token_is_not_enough_when_the_query_has_two()
    {
        // Both observed live in QA: a fondant spray gun (shares only "machine") and espresso CUPS
        // (share only "espresso") must not become "espresso machine" results.
        var (models, created) = ItemEnrichmentService.AppendCatalogDiscoveries(
            Existing("Krups Espresso Machine"),
            new[]
            {
                Entry("JJ110 Fondant Cakes Air Spray Gun chromatically machine airbrush"),
                Entry("Origin - Espresso tasses (2 x 80ml)")
            },
            storeName: "Ahlia", query: "buy espresso machine Jordan");

        created.Should().BeEmpty();
        models.Should().HaveCount(1);
    }

    [Fact]
    public void Multi_sku_pages_yield_distinct_variant_products()
    {
        // The owner's live find: one page, three AC SKUs — they must become THREE products, not
        // one price on one model. Numeric variant tokens (2 vs 3 ton) must keep them distinct.
        var (models, created) = ItemEnrichmentService.AppendCatalogDiscoveries(
            Existing("MEC AC 2 Ton Inverter"),
            new[]
            {
                Entry("MEC AC 1.5 Ton Inverter Split"),
                Entry("MEC AC 2 Ton Inverter Split"),   // matches the existing model — decorates, not duplicates
                Entry("MEC AC 3 Ton Inverter Split")
            },
            storeName: "MEC Store", query: "best acs in jordan", geo: "Jordan");

        created.Should().BeEquivalentTo(new[]
        {
            "MEC AC 1.5 Ton Inverter Split",
            "MEC AC 3 Ton Inverter Split"
        }, "the 2-ton line belongs to the existing model; 1.5 and 3 ton are new distinct products");
        models.Should().HaveCount(3);
    }

    [Fact]
    public void No_query_means_no_creation()
    {
        var (models, created) = ItemEnrichmentService.AppendCatalogDiscoveries(
            Existing("Anything"), new[] { Entry("Beko Espresso Machine") }, "Jo Cell", query: null);

        created.Should().BeEmpty("without query context there is no relevance gate, so nothing is invented");
        models.Should().HaveCount(1);
    }

    [Fact]
    public void Query_scoped_page_entries_bypass_the_name_gate_cross_language()
    {
        // QA live find: the store's own search for "rice cooker" returned an Arabic-named cooker —
        // "طنجرة أرز" shares zero tokens with "rice cooker", and the English-token gate dropped a
        // correct product. Entries from the store's OWN query-scoped search page are trusted: the
        // store's engine already matched them, in its own language.
        var (models, created) = ItemEnrichmentService.AppendCatalogDiscoveries(
            new List<ProductModel>(),
            new[] { Entry("طنجرة أرز كهربائية 1.8 لتر بطهي سريع وفعال") },
            storeName: "متجر عمان", query: "rice cooker in amman", geo: "jordan",
            fromQueryScopedPage: true);

        created.Should().ContainSingle().Which.Should().Contain("طنجرة أرز");
        models.Should().HaveCount(1);
    }

    [Fact]
    public void Domain_wide_entries_keep_the_name_gate()
    {
        // Same Arabic entry from a DOMAIN-WIDE catalogue (whole store, any category) must still be
        // gated — without the source page's query scoping, an unmatched name is just noise.
        var (models, created) = ItemEnrichmentService.AppendCatalogDiscoveries(
            new List<ProductModel>(),
            new[] { Entry("طنجرة أرز كهربائية 1.8 لتر") },
            storeName: "متجر عمان", query: "rice cooker in amman", geo: "jordan");

        created.Should().BeEmpty();
        models.Should().BeEmpty();
    }
}
