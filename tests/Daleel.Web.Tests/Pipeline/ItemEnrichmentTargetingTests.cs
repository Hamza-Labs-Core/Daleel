using Daleel.Core.Intelligence;
using Daleel.Core.Models;
using Daleel.Web.Pipeline;
using FluentAssertions;
using Xunit;

namespace Daleel.Web.Tests.Pipeline;

/// <summary>
/// The deep-dive's scrape-target choice. Official brand specs are the goal, so an offer on the
/// brand's own page wins over a cheaper marketplace listing; absent one, the cheapest offer is the
/// fallback (it at least has a real product page), then any offer with a URL.
/// </summary>
public class ItemEnrichmentTargetingTests
{
    [Fact]
    public void PrefersBrandOfficialPage_OverCheaperMarketplaceOffer()
    {
        var model = new ProductModel
        {
            Name = "Samsung AR24",
            Offers = new[]
            {
                new PriceOffer { Source = "OpenSooq", Url = "https://opensooq/x", Price = 400, IsLowest = true, SourceType = ResultType.Marketplace },
                new PriceOffer { Source = "Samsung Jordan", Url = "https://samsung.com/jo/ar24", Price = 520, SourceType = ResultType.BrandPage }
            }
        };

        ItemEnrichmentService.OfficialOrCheapestUrl(model).Should().Be("https://samsung.com/jo/ar24");
    }

    [Fact]
    public void FallsBackToCheapestOffer_WhenNoBrandPage()
    {
        var model = new ProductModel
        {
            Name = "LG DualCool",
            Offers = new[]
            {
                new PriceOffer { Source = "Store A", Url = "https://a/x", Price = 500, SourceType = ResultType.StorePage },
                new PriceOffer { Source = "Store B", Url = "https://b/x", Price = 450, IsLowest = true, SourceType = ResultType.StorePage }
            }
        };

        ItemEnrichmentService.OfficialOrCheapestUrl(model).Should().Be("https://b/x");
    }

    [Fact]
    public void ReturnsNull_WhenNoOfferHasUrl()
    {
        var model = new ProductModel
        {
            Name = "No links",
            Offers = new[] { new PriceOffer { Source = "X", Price = 100 } }
        };

        ItemEnrichmentService.OfficialOrCheapestUrl(model).Should().BeNull();
    }
}
