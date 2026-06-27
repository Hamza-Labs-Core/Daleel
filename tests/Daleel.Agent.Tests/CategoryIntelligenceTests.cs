using Daleel.Agent;
using Daleel.Core.Geo;
using Daleel.Core.Intelligence;
using FluentAssertions;
using Xunit;

namespace Daleel.Agent.Tests;

/// <summary>
/// Covers the up-front "thinking" pass: the category-intelligence prompt shape and the
/// <see cref="AgentService.AnalyzeCategoryAsync"/> parsing/fallback behaviour, plus that the
/// schema makes product extraction spec-aware.
/// </summary>
public class CategoryIntelligenceTests
{
    [Fact]
    public void CategoryIntelligence_AsksForTypeStoresBrandsAndSpecs()
    {
        var prompt = PromptTemplates.CategoryIntelligence("air conditioner", GeoProfiles.Jordan);

        prompt.Should().Contain("productType");
        prompt.Should().Contain("relevantStoreTypes");
        prompt.Should().Contain("expectedBrands");
        prompt.Should().Contain("specs");
        prompt.Should().Contain("higherIsBetter");
        // The market name must be in the prompt so brands/prices are grounded locally.
        prompt.Should().Contain("Jordan");
    }

    [Fact]
    public void ExtractProducts_WithSchema_EmbedsThePrioritySpecKeys()
    {
        var schema = new ProductSchema
        {
            ProductType = "air conditioner",
            Fields = new[]
            {
                new SpecField { Key = "btu", Label = "Cooling capacity", Unit = "BTU", Importance = SpecImportance.Key },
                new SpecField { Key = "energy_rating", Label = "Energy rating", Importance = SpecImportance.Key }
            }
        };

        var prompt = PromptTemplates.ExtractProducts("ACs", GeoProfiles.Jordan, "ctx", schema);

        prompt.Should().Contain("air conditioner");
        prompt.Should().Contain("btu");
        prompt.Should().Contain("energy_rating");
        prompt.Should().Contain("Cooling capacity");
    }

    [Fact]
    public void ExtractProducts_WithoutSchema_DoesNotEmitPrioritySpecsBlock()
    {
        var prompt = PromptTemplates.ExtractProducts("ACs", GeoProfiles.Jordan, "ctx");
        prompt.Should().NotContain("PRIORITISE these fields");
    }

    [Fact]
    public async Task AnalyzeCategoryAsync_ParsesIntelligenceAndSchema()
    {
        const string json = """
            {
              "productType": "Air Conditioner",
              "relevantStoreTypes": ["electronics store", "HVAC retailer"],
              "expectedBrands": ["Gree", "Samsung", "LG"],
              "priceExpectation": "typically 250-1200 JOD",
              "imagesMatter": true,
              "specs": [
                {"key": "BTU", "label": "Cooling capacity", "unit": "BTU", "higherIsBetter": true, "importance": "key"},
                {"key": "energy_rating", "label": "Energy rating", "higherIsBetter": true, "importance": "normal"}
              ],
              "reasoning": "ACs are compared on cooling capacity and efficiency."
            }
            """;
        var agent = new AgentService(new FakeLlmClient(_ => json));

        var intel = await agent.AnalyzeCategoryAsync("AC", GeoProfiles.Jordan);

        intel.ProductType.Should().Be("air conditioner"); // normalized to lower case
        intel.RelevantStoreTypes.Should().Contain("HVAC retailer");
        intel.ExpectedBrands.Should().HaveCount(3);
        intel.ImagesMatter.Should().BeTrue();
        intel.IsEmpty.Should().BeFalse();

        intel.Schema.Fields.Should().HaveCount(2);
        var btu = intel.Schema.Fields[0];
        btu.Key.Should().Be("btu"); // normalized to lower snake-case
        btu.Importance.Should().Be(SpecImportance.Key);
        btu.HigherIsBetter.Should().BeTrue();
    }

    [Fact]
    public async Task AnalyzeCategoryAsync_FallsBackToNeutralOnUnparseableJson()
    {
        var agent = new AgentService(new FakeLlmClient(_ => "not json at all"));

        var intel = await agent.AnalyzeCategoryAsync("AC", GeoProfiles.Jordan);

        intel.Category.Should().Be("AC");
        intel.IsEmpty.Should().BeTrue();
        intel.Schema.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public async Task CompareAsync_PopulatesSchemaFromCategoryIntelligence()
    {
        const string strategy = """
            {"queryType":"Comparison","subject":"smartphone","webQueries":["x"],"shoppingQueries":[],
             "socialQueries":[],"placesQueries":[],"urlsToRead":[],"reasoning":"compare"}
            """;
        const string schema = """
            {"productType":"smartphone","relevantStoreTypes":["electronics store"],
             "expectedBrands":["Apple","Samsung"],"imagesMatter":true,
             "specs":[{"key":"ram_gb","label":"RAM","unit":"GB","higherIsBetter":true,"importance":"key"}],
             "reasoning":"phones compare on RAM, storage, camera"}
            """;
        var llm = new FakeLlmClient(system =>
            system == PromptTemplates.PlannerSystem ? strategy
            : system == PromptTemplates.CategoryIntelligenceSystem ? schema
            : "comparison summary");
        var agent = new AgentService(llm);

        var result = await agent.CompareAsync(new[] { "iPhone 15", "Galaxy S24" }, "jordan");

        result.Schema.ProductType.Should().Be("smartphone");
        result.Schema.Fields.Should().ContainSingle(f => f.Key == "ram_gb" && f.Label == "RAM");
    }
}
