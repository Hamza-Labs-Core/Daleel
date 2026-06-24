using Daleel.Core.Models;
using Daleel.Pipeline.Extraction;
using FluentAssertions;
using Xunit;

namespace Daleel.Pipeline.Tests;

public class ComparisonGrouperTests
{
    private static ProductListing L(string name, decimal price, string currency = "JOD", decimal? original = null) =>
        new() { Name = name, Price = price, Currency = currency, OriginalPrice = original };

    [Fact]
    public void Group_SplitsIntoThreeTiersByPrice()
    {
        var listings = new[]
        {
            L("A", 200), L("B", 250), L("C", 400), L("D", 450), L("E", 800), L("F", 900),
        };

        var groups = ComparisonGrouper.Group(listings);

        groups.Select(g => g.Category).Should().ContainInOrder("Budget", "Mid-range", "Premium");
        groups[0].Items.Should().OnlyContain(l => l.Price <= 250);
        groups[2].Items.Should().OnlyContain(l => l.Price >= 800);
    }

    [Fact]
    public void Group_RecommendsCheapestAsBestValue()
    {
        var groups = ComparisonGrouper.Group(new[] { L("Cheap", 200), L("Mid", 250), L("X", 400), L("Y", 450), L("Z", 800), L("W", 900) });

        groups[0].Recommendation.Should().Contain("Best value").And.Contain("Cheap");
    }

    [Fact]
    public void Group_FlagsDeals()
    {
        var groups = ComparisonGrouper.Group(new[]
        {
            L("OnSale", 180, original: 240), L("B", 250), L("C", 400), L("D", 450), L("E", 800), L("F", 900),
        });

        groups[0].Recommendation.Should().Contain("Best deal").And.Contain("OnSale");
    }

    [Fact]
    public void Group_TooFew_ReturnsSingleSet()
    {
        var groups = ComparisonGrouper.Group(new[] { L("A", 200), L("B", 300) });

        groups.Should().ContainSingle();
        groups[0].Category.Should().Be("All options");
    }

    [Fact]
    public void Group_OnlyDominantCurrency_IsTiered()
    {
        var listings = new[]
        {
            L("A", 200, "JOD"), L("B", 250, "JOD"), L("C", 400, "JOD"),
            L("D", 450, "JOD"), L("E", 800, "JOD"), L("USD-one", 999, "USD"),
        };

        var groups = ComparisonGrouper.Group(listings);

        groups.SelectMany(g => g.Items).Should().OnlyContain(l => l.Currency == "JOD");
    }

    [Fact]
    public void Group_NoPricedListings_ReturnsEmpty()
    {
        var listings = new[] { new ProductListing { Name = "No price" } };
        ComparisonGrouper.Group(listings).Should().BeEmpty();
    }
}
