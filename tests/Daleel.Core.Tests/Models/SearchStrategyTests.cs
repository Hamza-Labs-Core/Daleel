using System.Text.Json;
using System.Text.Json.Serialization;
using Daleel.Core.Models;
using FluentAssertions;
using Xunit;

namespace Daleel.Core.Tests.Models;

/// <summary>
/// The search-object fields on SearchStrategy: defaults are empty (fully additive), and the
/// record round-trips through System.Text.Json — it is persisted inside SearchJob.ResultJson,
/// so old JSON without the new fields MUST deserialize to the same empty defaults.
/// </summary>
public class SearchStrategyTests
{
    /// <summary>
    /// Mirrors Daleel.Web.Services.ResultSerialization.Options (the persistence path for
    /// SearchJob.ResultJson). That type lives in Daleel.Web, which this test project can't
    /// reference, so the configuration is duplicated here: compact output, nulls omitted,
    /// enums as names. Keep in sync if ResultSerialization ever changes.
    /// </summary>
    private static readonly JsonSerializerOptions PersistenceOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public void NewFields_DefaultToEmpty()
    {
        var s = new SearchStrategy();
        s.Product.Should().BeEmpty();
        s.Specs.Should().BeEmpty();
        s.Location.Should().BeEmpty();
        s.Goal.Should().BeEmpty();
        s.Facets.Should().BeEmpty();
        s.DefaultSort.Should().BeEmpty();
    }

    [Fact]
    public void RoundTrips_ThroughJson()
    {
        var s = new SearchStrategy
        {
            Product = "diapers",
            Specs = new Dictionary<string, string> { ["size"] = "4" },
            Location = "Amman",
            Goal = "cheapest",
            DefaultSort = "price_asc",
            Facets = new[]
            {
                new SearchFacet { Key = "size", Label = "Size", Unit = null, Values = new[] { "3", "4", "5" } }
            }
        };

        var back = JsonSerializer.Deserialize<SearchStrategy>(
            JsonSerializer.Serialize(s, PersistenceOptions), PersistenceOptions)!;
        back.Product.Should().Be("diapers");
        back.Specs["size"].Should().Be("4");
        back.Goal.Should().Be("cheapest");
        back.DefaultSort.Should().Be("price_asc");
        back.Facets.Should().ContainSingle(f => f.Key == "size" && f.Values.Count == 3);
    }

    [Fact]
    public void OldJson_WithoutNewFields_DeserializesToEmptyDefaults()
    {
        const string oldJson = """{ "QueryType": 0, "Subject": "AC", "WebQueries": ["best AC"] }""";
        var s = JsonSerializer.Deserialize<SearchStrategy>(oldJson, PersistenceOptions)!;
        s.Subject.Should().Be("AC");
        s.Product.Should().BeEmpty();
        s.Facets.Should().BeEmpty();
        s.DefaultSort.Should().BeEmpty();
    }
}
