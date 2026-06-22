using Daleel.Core.Models;
using Daleel.Search.Aggregation;
using FluentAssertions;
using Xunit;

namespace Daleel.Search.Tests;

public class MarketplaceAggregatorTests
{
    private static PricePoint P(string product, decimal amount, string currency, string store) =>
        new() { Product = product, Price = new Money(amount, currency), Store = store };

    [Fact]
    public void Merge_RemovesDuplicateOffers()
    {
        var a = new[] { P("Samsung AC", 450, "JOD", "OpenSooq") };
        var b = new[] { P("Samsung AC", 450, "JOD", "OpenSooq"), P("Samsung AC", 470, "JOD", "Carrefour") };

        var merged = MarketplaceAggregator.Merge(a, b);

        merged.Should().HaveCount(2);
    }

    [Fact]
    public void Merge_TreatsNormalizedTitlesAsEqual()
    {
        // Same words, different diacritics → same dedup key.
        var a = new[] { P("مكيف سامسونج", 450, "JOD", "OpenSooq") };
        var b = new[] { P("مُكيّف سامسونج", 450, "JOD", "OpenSooq") };

        MarketplaceAggregator.Merge(a, b).Should().ContainSingle();
    }

    [Fact]
    public void Compare_ComputesLowestHighestMedian()
    {
        var offers = new[]
        {
            P("AC", 450, "JOD", "A"),
            P("AC", 599, "JOD", "B"),
            P("AC", 500, "JOD", "C"),
        };

        var cmp = MarketplaceAggregator.Compare("AC", offers);

        cmp.Lowest!.Value.Amount.Should().Be(450);
        cmp.Highest!.Value.Amount.Should().Be(599);
        cmp.Median!.Value.Amount.Should().Be(500);
        cmp.StoreCount.Should().Be(3);
    }

    [Fact]
    public void Compare_PicksDominantCurrencyGroup()
    {
        var offers = new[]
        {
            P("AC", 450, "JOD", "A"),
            P("AC", 500, "JOD", "B"),
            P("AC", 700, "USD", "C"), // minority currency, excluded from comparison
        };

        var cmp = MarketplaceAggregator.Compare("AC", offers);

        cmp.Lowest!.Value.Currency.Should().Be("JOD");
        cmp.Offers.Should().OnlyContain(o => o.Price.Currency == "JOD");
    }

    [Fact]
    public void Compare_IgnoresZeroPricedOffers()
    {
        var offers = new[]
        {
            P("AC", 0, "JOD", "A"),
            P("AC", 450, "JOD", "B"),
        };

        var cmp = MarketplaceAggregator.Compare("AC", offers);
        cmp.Lowest!.Value.Amount.Should().Be(450);
    }

    [Fact]
    public void Compare_EmptyOffers_ReturnsNulls()
    {
        var cmp = MarketplaceAggregator.Compare("AC", Array.Empty<PricePoint>());
        cmp.Lowest.Should().BeNull();
        cmp.StoreCount.Should().Be(0);
    }
}
