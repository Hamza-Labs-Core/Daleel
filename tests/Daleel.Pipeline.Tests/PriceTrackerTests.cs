using Daleel.Core.Models;
using Daleel.Pipeline;
using FluentAssertions;
using Xunit;

namespace Daleel.Pipeline.Tests;

public class PriceTrackerTests
{
    [Fact]
    public void ExtractPrices_FindsMultiplePricesInText()
    {
        var tracker = new PriceTracker("JOD");
        var prices = tracker.ExtractPrices("الموديل الأول بسعر 450 دينار والموديل الثاني 599 دينار");

        prices.Should().HaveCount(2);
        prices.Select(p => p.Amount).Should().Contain(new[] { 450m, 599m });
    }

    [Fact]
    public void ExtractPrices_IgnoresBareCountsWithoutCurrency()
    {
        var tracker = new PriceTracker("USD");
        // "5 reviews" and "10 people" are not prices.
        var prices = tracker.ExtractPrices("Rated by 5 reviews, recommended by 10 people");
        prices.Should().BeEmpty();
    }

    [Fact]
    public void ExtractPrices_AcceptsDecimalWithoutCurrencyWord()
    {
        var tracker = new PriceTracker("USD");
        var prices = tracker.ExtractPrices("On sale for 19.99 today only");
        prices.Should().ContainSingle();
        prices[0].Amount.Should().Be(19.99m);
    }

    [Fact]
    public void TrackFromText_RecordsPointsForProduct()
    {
        var tracker = new PriceTracker("JOD");
        tracker.TrackFromText("Samsung AC", "متوفر بسعر 450 دينار في المتجر", store: "OpenSooq");

        tracker.Points.Should().ContainSingle();
        tracker.Points[0].Product.Should().Be("Samsung AC");
        tracker.Points[0].Store.Should().Be("OpenSooq");
    }

    [Fact]
    public void LowestAndHighest_ComputeWithinDominantCurrency()
    {
        var tracker = new PriceTracker("JOD");
        tracker.Track(new PricePoint { Product = "x", Price = new Money(450, "JOD") });
        tracker.Track(new PricePoint { Product = "x", Price = new Money(599, "JOD") });
        tracker.Track(new PricePoint { Product = "x", Price = new Money(380, "JOD") });

        tracker.Lowest()!.Value.Amount.Should().Be(380);
        tracker.Highest()!.Value.Amount.Should().Be(599);
    }

    [Fact]
    public void Lowest_NullWhenNoPrices()
    {
        new PriceTracker().Lowest().Should().BeNull();
    }
}
