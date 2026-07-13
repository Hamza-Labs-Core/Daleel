using Daleel.Core.Models;
using Daleel.Web.Data;
using Daleel.Web.Pipeline;
using FluentAssertions;
using Xunit;

namespace Daleel.Web.Tests.Pipeline;

// Stage 1 of the pipeline-cascade spec: a discovered brand's catalogue must become GRID PRODUCTS,
// not just enrich existing ones (the "hundreds of items" lever). BrandModel rows (harvested into the
// brand DB) map to the same entry tuple AppendCatalogDiscoveries already turns into ProductModels —
// so the relevance gate + dedup that guards store catalogues guards brand catalogues too, for free.
public class BrandCatalogGridTests
{
    private static BrandModel Model(string name, decimal? local = null, string? img = null, string? url = null) =>
        new() { ModelName = name, LocalPrice = local, Currency = "JOD", ImageUrl = img, SourceUrl = url };

    [Fact]
    public void Maps_brand_models_to_catalogue_entries_keeping_price_image_url()
    {
        var entries = ItemEnrichmentService.BrandCatalogEntries(new[]
        {
            Model("De'Longhi Magnifica S ECAM220", local: 429m, img: "https://b/i.jpg", url: "https://delonghi.jo/ecam220"),
        }).ToList();

        entries.Should().HaveCount(1);
        var e = entries[0];
        e.Name.Should().Be("De'Longhi Magnifica S ECAM220");
        e.Price.Should().Be(429m);
        e.Currency.Should().Be("JOD");
        e.Image.Should().Be("https://b/i.jpg");
        e.Url.Should().Be("https://delonghi.jo/ecam220");
        e.Indicative.Should().BeTrue("a brand-site catalogue price is a lead to verify, not a live local offer");
    }

    [Fact]
    public void Skips_models_with_no_name()
    {
        ItemEnrichmentService.BrandCatalogEntries(new[] { Model("  "), Model("Krups EA910") })
            .Select(e => e.Name).Should().Equal("Krups EA910");
    }

    [Fact]
    public void Fed_through_AppendCatalogDiscoveries_only_query_relevant_models_become_grid_items()
    {
        // A brand's full catalogue mixes coffee machines with toasters; a "coffee machine" search must
        // only gain the coffee machines. The relevance gate lives in AppendCatalogDiscoveries — this
        // proves the brand path inherits it.
        var brandModels = new[]
        {
            Model("De'Longhi Dedica Espresso Coffee Machine EC685", img: "https://b/1.jpg", url: "https://d.jo/1"),
            Model("De'Longhi Icona 4-slice Toaster CTOV4003", img: "https://b/2.jpg", url: "https://d.jo/2"),
        };
        var entries = ItemEnrichmentService.BrandCatalogEntries(brandModels);

        var (grid, created) = ItemEnrichmentService.AppendCatalogDiscoveries(
            new List<ProductModel>(), entries, storeName: "De'Longhi", query: "coffee machine", geo: "jordan");

        created.Should().ContainSingle().Which.Should().Contain("Espresso Coffee Machine");
        grid.Should().ContainSingle(m => m.Name.Contains("EC685"));
        grid.Should().NotContain(m => m.Name.Contains("Toaster"));
    }
}
