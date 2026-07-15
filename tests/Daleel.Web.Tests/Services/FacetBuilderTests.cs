using Daleel.Core.Intelligence;
using Daleel.Core.Models;
using Daleel.Web.Services;
using FluentAssertions;
using Xunit;

namespace Daleel.Web.Tests.Services;

/// <summary>
/// FacetBuilder turns the search object's facet dimensions into renderable facets whose options
/// are the union of planner candidates and the values actually present in the results. Facets are
/// ALWAYS emitted (never hidden for sparseness); spec keys match loosely (case + separators).
/// </summary>
public class FacetBuilderTests
{
    private static ProductModel WithSpecs(params (string K, string V)[] specs) => new()
    {
        Name = "m",
        Specs = specs.ToDictionary(s => s.K, s => s.V)
    };

    [Fact]
    public void Options_AreUnionOfPlannerValuesAndResultSpecs()
    {
        var strategy = new SearchStrategy
        {
            Facets = new[] { new SearchFacet { Key = "size", Label = "Size", Values = new[] { "3", "4" } } }
        };
        var models = new[] { WithSpecs(("Size", "5")), WithSpecs(("size", "4")) };

        var facets = FacetBuilder.Build(strategy, ProductSchema.General, models);

        facets.Should().ContainSingle();
        facets[0].Options.Should().BeEquivalentTo(new[] { "3", "4", "5" });
    }

    [Fact]
    public void SpecKeyMatching_IgnoresCaseAndSeparators()
    {
        var strategy = new SearchStrategy
        {
            Facets = new[] { new SearchFacet { Key = "screen size", Label = "Screen size" } }
        };
        // Result specs use snake_case (the schema convention) and title case — both must bind.
        var models = new[] { WithSpecs(("screen_size", "55")), WithSpecs(("Screen Size", "65")) };

        var facets = FacetBuilder.Build(strategy, ProductSchema.General, models);
        facets[0].Options.Should().BeEquivalentTo(new[] { "55", "65" });
    }

    [Fact]
    public void EmptyFacet_IsStillEmitted()
    {
        var strategy = new SearchStrategy
        {
            Facets = new[] { new SearchFacet { Key = "count", Label = "Pack count" } }
        };
        var facets = FacetBuilder.Build(strategy, ProductSchema.General, Array.Empty<ProductModel>());

        facets.Should().ContainSingle(); // always shown, even with zero options
        facets[0].Options.Should().BeEmpty();
    }

    [Fact]
    public void NoStrategyFacets_FallsBackToProductSchemaFields()
    {
        var schema = new ProductSchema
        {
            ProductType = "air conditioner",
            Fields = new[]
            {
                new SpecField { Key = "btu", Label = "BTU", Importance = SpecImportance.Key },
                new SpecField { Key = "noise_db", Label = "Noise", Importance = SpecImportance.Normal }
            }
        };
        var models = new[] { WithSpecs(("btu", "12000")), WithSpecs(("btu", "18000")) };

        var facets = FacetBuilder.Build(new SearchStrategy(), schema, models);

        facets.Should().HaveCount(2);
        facets[0].Key.Should().Be("btu"); // Key-importance fields lead
        facets[0].Options.Should().BeEquivalentTo(new[] { "12000", "18000" });
    }

    [Fact]
    public void NullStrategy_AndEmptySchema_YieldNoFacets() =>
        FacetBuilder.Build(null, ProductSchema.General, Array.Empty<ProductModel>()).Should().BeEmpty();

    [Fact]
    public void Matches_FiltersModelBySelectedValue()
    {
        var model = WithSpecs(("Size", " 4 "));
        FacetBuilder.Matches(model, "size", "4").Should().BeTrue();   // trim + case-insensitive
        FacetBuilder.Matches(model, "size", "5").Should().BeFalse();
        FacetBuilder.Matches(model, "missing", "4").Should().BeFalse();
    }

    [Fact]
    public void Build_SkipsPlannerFacetsDuplicatingBuiltInFilters()
    {
        // The grid always renders Brand/Source/Condition selects and price fields; a planner-named
        // "Brand" or "Condition" facet would show the same control twice (QA: washing-machines run).
        var strategy = new SearchStrategy
        {
            Facets = new[]
            {
                new SearchFacet { Key = "Brand", Label = "Brand" },
                new SearchFacet { Key = "condition", Label = "Condition" },
                new SearchFacet { Key = "Price (JOD)", Label = "Price" },
                new SearchFacet { Key = "capacity", Label = "Capacity", Unit = "kg" }
            }
        };

        var facets = FacetBuilder.Build(strategy, ProductSchema.General, Array.Empty<ProductModel>());

        facets.Should().ContainSingle();
        facets[0].Key.Should().Be("capacity");
    }

    [Fact]
    public void Options_DedupeUnitSuffixedValues()
    {
        // QA: the Capacity (kg) facet offered both "9" (planner) and "9 Kg" (result spec) as
        // separate options. With the facet's declared unit, they canonicalize to one option.
        var strategy = new SearchStrategy
        {
            Facets = new[] { new SearchFacet { Key = "capacity", Label = "Capacity", Unit = "kg", Values = new[] { "9" } } }
        };
        var models = new[] { WithSpecs(("Capacity", "9 Kg")), WithSpecs(("capacity", "9KG")) };

        var facets = FacetBuilder.Build(strategy, ProductSchema.General, models);

        facets[0].Options.Should().BeEquivalentTo(new[] { "9" });
    }

    [Fact]
    public void Matches_IsUnitTolerant_WhenFacetDeclaresAUnit()
    {
        var model = WithSpecs(("Capacity", "9 Kg"));
        FacetBuilder.Matches(model, "capacity", "9", unit: "kg").Should().BeTrue();
        FacetBuilder.Matches(model, "capacity", "9 kg", unit: "kg").Should().BeTrue(); // both sides canonicalized
        FacetBuilder.Matches(model, "capacity", "10", unit: "kg").Should().BeFalse();
        // No unit declared → exact behavior unchanged ("9 Kg" ≠ "9").
        FacetBuilder.Matches(model, "capacity", "9").Should().BeFalse();
    }
}
