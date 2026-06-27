using Daleel.Core.Intelligence;
using Daleel.Web.Identification;
using FluentAssertions;
using Xunit;

namespace Daleel.Web.Tests.Identification;

/// <summary>
/// Pins the merge contract: cross-source de-duplication, unit normalization, conflict resolution by source
/// authority (brand site wins), and schema-driven re-keying/ordering.
/// </summary>
public class SpecMergerTests
{
    private static readonly ISpecMerger Merger = new SpecMerger();

    [Fact]
    public void Merge_DeduplicatesSameAttributeAcrossSources_KeyedConsistently()
    {
        var store = SpecSource.Store(new Dictionary<string, string> { ["Screen Size"] = "55\"" });
        var brand = SpecSource.Brand(new Dictionary<string, string> { ["screen_size"] = "55 inches" });

        var merged = Merger.Merge(new[] { store, brand });

        merged.Should().ContainSingle();
        merged["screen_size"].Should().Be("55 inches / 139.7 cm");
    }

    [Fact]
    public void Merge_ResolvesConflicts_PreferringTheBrandSite()
    {
        // The store and the brand disagree on RAM; the brand site is authoritative and must win.
        var store = SpecSource.Store(new Dictionary<string, string> { ["ram"] = "8 GB" });
        var brand = SpecSource.Brand(new Dictionary<string, string> { ["ram"] = "12 GB" });

        var merged = Merger.Merge(new[] { store, brand });

        merged["ram"].Should().Be("12 GB");
    }

    [Fact]
    public void Merge_NormalizesKeysSoVariantsCollapse()
    {
        var a = SpecSource.Store(new Dictionary<string, string> { ["Energy Rating"] = "A++" });
        var b = SpecSource.Review(new Dictionary<string, string> { ["energy-rating"] = "A+" });

        var merged = Merger.Merge(new[] { a, b });

        merged.Should().ContainSingle();
        merged.Should().ContainKey("energy_rating");
        merged["energy_rating"].Should().Be("A++", "the higher-priority store source wins the conflict");
    }

    [Fact]
    public void Merge_AppliesSchema_RekeyingByLabelAndOrderingSchemaFirst()
    {
        var schema = new ProductSchema
        {
            ProductType = "smartphone",
            Fields = new[]
            {
                new SpecField { Key = "screen_size", Label = "Screen Size" },
                new SpecField { Key = "ram", Label = "RAM" }
            }
        };

        var store = SpecSource.Store(new Dictionary<string, string>
        {
            ["Warranty"] = "2 years",     // not in the schema → appended after
            ["Screen Size"] = "6.8 inch", // matches by label → re-keyed to screen_size
            ["RAM"] = "12 GB"
        });

        var merged = Merger.Merge(new[] { store }, schema);

        merged.Keys.Should().ContainInOrder("screen_size", "ram", "warranty");
        merged["screen_size"].Should().Be("6.8 inches / 17.3 cm");
    }

    [Fact]
    public void Merge_DropsEmptyKeysAndValues()
    {
        var store = SpecSource.Store(new Dictionary<string, string>
        {
            [""] = "ignored",
            ["color"] = "   ",
            ["weight"] = "1.2 kg"
        });

        var merged = Merger.Merge(new[] { store });

        merged.Should().ContainSingle().And.ContainKey("weight");
    }

    [Fact]
    public void Merge_WithNoSources_IsEmpty()
    {
        Merger.Merge(Array.Empty<SpecSource>()).Should().BeEmpty();
    }
}
