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
    private static (string, decimal?, string?, string?, string?, bool) Entry(
        string name, decimal? price = 99m, string? url = "https://jo-cell.com/products/x") =>
        (name, price, "JOD", url, null, true);

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
    public void No_query_means_no_creation()
    {
        var (models, created) = ItemEnrichmentService.AppendCatalogDiscoveries(
            Existing("Anything"), new[] { Entry("Beko Espresso Machine") }, "Jo Cell", query: null);

        created.Should().BeEmpty("without query context there is no relevance gate, so nothing is invented");
        models.Should().HaveCount(1);
    }
}
