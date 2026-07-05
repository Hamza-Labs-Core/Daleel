using Daleel.Core.Models;
using Daleel.Web.Pipeline.Enrichment;
using FluentAssertions;
using Xunit;

namespace Daleel.Web.Tests.Pipeline;

/// <summary>
/// Pins the "you have the site, get the price" picker: the priced line naming the model wins as an
/// EXACT figure; a page with prices that name nothing yields its leading price as indicative.
/// </summary>
public class PriceFetchTests
{
    private static readonly ProductModel Dedica = new()
    {
        Name = "DeLonghi Espresso Maker Dedica Style", Brand = "DeLonghi", Model = "EC685"
    };

    [Fact]
    public void Line_naming_the_model_wins_as_exact()
    {
        var page = """
            Related: Moka pot classic 12.50 JOD
            DeLonghi Dedica Style EC685 espresso maker — 175.00 JOD in stock
            Delivery from 3.00 JOD
            """;

        var picked = PriceFetchHandler.PickPrice(page, Dedica);

        picked.Should().NotBeNull();
        picked!.Value.Price.Should().Be(175.00m);
        picked.Value.Exact.Should().BeTrue("the priced line names the model");
    }

    [Fact]
    public void Unnamed_prices_fall_back_to_the_pages_leading_price_as_indicative()
    {
        var page = "Special offer today only: 99.00 JOD. Free delivery over 25.00 JOD.";

        var picked = PriceFetchHandler.PickPrice(page, Dedica);

        picked.Should().NotBeNull();
        picked!.Value.Price.Should().Be(99.00m);
        picked.Value.Exact.Should().BeFalse("nothing on the page names the model — a lead, not a quote");
    }

    [Fact]
    public void No_prices_means_null()
    {
        PriceFetchHandler.PickPrice("Beautiful espresso machines. Contact us for pricing.", Dedica)
            .Should().BeNull();
    }
}
