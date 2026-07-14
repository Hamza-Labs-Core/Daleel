using Daleel.Core.Models;
using Daleel.Web.Services;
using FluentAssertions;
using Xunit;

namespace Daleel.Web.Tests.Services;

/// <summary>
/// The fuzzy offer→store join behind per-card store reviews. No shared id exists, so it matches
/// normalized store names (offer Source/Seller vs StoreInfo.Name) with a website-domain fallback
/// (offer Url host vs store Url host). Unmatched stores contribute nothing — a clean miss.
/// </summary>
public class StoreReviewMatcherTests
{
    private static StoreInfo Store(string name, string? url = null, params string[] reviewTexts) => new()
    {
        Name = name,
        Url = url,
        Reviews = reviewTexts.Select(t => new StoreReview { Text = t }).ToList()
    };

    private static ProductModel WithOffers(params PriceOffer[] offers) =>
        new() { Name = "m", Offers = offers };

    [Fact]
    public void MatchesOfferSource_ToStoreName_CaseAndWhitespaceInsensitive()
    {
        var model = WithOffers(new PriceOffer { Source = "  SmartBuy " });
        var stores = new[] { Store("smartbuy", null, "great store") };

        StoreReviewMatcher.ReviewsFor(model, stores)
            .Should().ContainSingle(r => r.Text == "great store");
    }

    [Fact]
    public void FallsBackToDomainMatch_WhenNamesDiffer()
    {
        var model = WithOffers(new PriceOffer { Source = "Offer Src", Url = "https://www.mumz.io/p/123" });
        var stores = new[] { Store("Mumzworld", "https://mumz.io", "fast delivery") };

        StoreReviewMatcher.ReviewsFor(model, stores)
            .Should().ContainSingle(r => r.Text == "fast delivery");
    }

    [Fact]
    public void UnmatchedStore_ContributesNothing()
    {
        var model = WithOffers(new PriceOffer { Source = "Alpha", Url = "https://alpha.jo/x" });
        var stores = new[] { Store("Beta", "https://beta.jo", "irrelevant") };

        StoreReviewMatcher.ReviewsFor(model, stores).Should().BeEmpty();
    }

    [Fact]
    public void DeduplicatesAcrossMultipleMatchingOffers()
    {
        var model = WithOffers(
            new PriceOffer { Source = "Beta" },
            new PriceOffer { Seller = "beta" });
        var stores = new[] { Store("Beta", null, "one review") };

        StoreReviewMatcher.ReviewsFor(model, stores).Should().HaveCount(1);
    }
}
