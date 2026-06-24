using Daleel.Core.Intelligence;
using Daleel.Core.Models;
using Daleel.Pipeline.Extraction;
using FluentAssertions;
using Xunit;

namespace Daleel.Pipeline.Tests;

public class ListingAggregatorTests
{
    private static ProductListing L(
        string name, string? brand, string? model, decimal? price, string? source,
        decimal? original = null, string? availability = null) =>
        new()
        {
            Name = name, Brand = brand, Model = model, Price = price, Currency = price is null ? null : "JOD",
            Source = source, OriginalPrice = original, Availability = availability, SourceType = ResultType.Marketplace
        };

    [Fact]
    public void Aggregate_CollapsesSameModelIntoOneEntryWithManyOffers()
    {
        var listings = new[]
        {
            L("Samsung AR24 (used)", "Samsung", "AR24TXHQ", 420, "OpenSooq"),
            L("Samsung AR24 Split", "Samsung", "ar24txhq", 550, "Samsung Jordan"),
            L("Samsung AR24", "Samsung", "AR24TXHQ", 499, "Carrefour"),
            L("LG Dual Inverter S4", "LG", "S4-Q12", 410, "Xcite"),
        };

        var models = ListingAggregator.Aggregate(listings);

        // Samsung AR24 (3 sources) and LG (1) → 2 models.
        models.Should().HaveCount(2);

        var samsung = models.First(m => m.Brand == "Samsung");
        samsung.SellerCount.Should().Be(3);
        samsung.Offers.Select(o => o.Source).Should().Contain(new[] { "OpenSooq", "Samsung Jordan", "Carrefour" });
    }

    [Fact]
    public void Aggregate_SortsOffersByPriceAndFlagsLowest()
    {
        var listings = new[]
        {
            L("AC", "B", "M1", 550, "Store A"),
            L("AC", "B", "M1", 420, "Store B"),
            L("AC", "B", "M1", 499, "Store C"),
        };

        var model = ListingAggregator.Aggregate(listings).Single();

        model.Offers.Select(o => o.Price).Should().ContainInOrder(420m, 499m, 550m);
        model.Offers.First().IsLowest.Should().BeTrue();
        model.Offers.First().Tags.Should().Contain("LOWEST");
        model.Offers.Skip(1).Should().OnlyContain(o => !o.IsLowest);
        model.LowestPrice.Should().Be(420);
    }

    [Fact]
    public void Aggregate_FlagsDealAndFreeShipping()
    {
        var listings = new[]
        {
            L("AC", "B", "M1", 400, "Store A", original: 520, availability: "In stock - Free shipping"),
            L("AC", "B", "M1", 450, "Store B"),
        };

        var model = ListingAggregator.Aggregate(listings).Single();
        var top = model.Offers.First();

        top.Tags.Should().Contain("SALE");
        top.Tags.Should().Contain("FREE SHIPPING");
    }

    [Fact]
    public void Aggregate_MergesSpecsAcrossSources()
    {
        var a = new ProductListing
        {
            Name = "AC", Brand = "B", Model = "M1", Source = "A",
            Specs = new Dictionary<string, string> { ["cooling_capacity"] = "24000 BTU" }
        };
        var b = new ProductListing
        {
            Name = "AC", Brand = "B", Model = "M1", Source = "B",
            Specs = new Dictionary<string, string> { ["energy_rating"] = "A++" }
        };

        var model = ListingAggregator.Aggregate(new[] { a, b }).Single();

        model.Specs.Should().ContainKey("cooling_capacity");
        model.Specs.Should().ContainKey("energy_rating");
    }
}
