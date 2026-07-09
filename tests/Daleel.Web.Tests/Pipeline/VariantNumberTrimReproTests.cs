using Daleel.Core.Models;
using Daleel.Web.Pipeline;
using FluentAssertions;
using Xunit;

namespace Daleel.Web.Tests.Pipeline;

/// <summary>
/// Regression: VariantNumbers must keep integer variants distinct (10 ≠ 100 ≠ 1) while still
/// normalizing decimal fractions (2.0 ≡ 2). The original TrimEnd('.', '0') collapsed 10/100 to "1",
/// making a 100L and a 10L heater read as the same SKU (branch review, matching-quality).
/// </summary>
public class VariantNumberTrimReproTests
{
    [Fact]
    public void VariantNumbers_preserves_distinct_integers()
    {
        // Intended semantics: "2.0" ≡ "2", but "10" must stay "10".
        ItemEnrichmentService.VariantNumbers("Ariston water heater 10")
            .Should().BeEquivalentTo(new[] { "10" }, "10 is a real capacity, not 1");
        ItemEnrichmentService.VariantNumbers("Ariston water heater 100")
            .Should().BeEquivalentTo(new[] { "100" });
        ItemEnrichmentService.VariantNumbers("Heater 10.00 L")
            .Should().BeEquivalentTo(new[] { "10" }, "10.00 normalizes to 10, not 1");
    }

    [Fact]
    public void Sibling_with_different_integer_capacity_disagrees()
    {
        ItemEnrichmentService.VariantsDisagree(
                "Ariston water heater 100", "Ariston water heater 10")
            .Should().BeTrue("100L and 10L are different SKUs");
    }

    [Fact]
    public void Catalog_100L_entry_becomes_its_own_model_next_to_the_10L()
    {
        var existing = new List<ProductModel> { new() { Name = "Ariston water heater 10" } };
        var (models, created) = ItemEnrichmentService.AppendCatalogDiscoveries(
            existing,
            new[] { ("Ariston water heater 100", (decimal?)120m, (string?)"JOD",
                     (string?)"https://store.example/wh100", (string?)null, true) },
            storeName: "Store", query: "water heaters in Jordan");

        created.Should().ContainSingle(n => n.Contains("100"),
            "the 100L unit is a distinct SKU and must get its own card");
        models.Should().HaveCount(2);
    }
}
