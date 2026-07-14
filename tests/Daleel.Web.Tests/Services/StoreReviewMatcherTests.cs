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

    [Fact]
    public void MatchesArabicStoreNames()
    {
        // Guards against a future ASCII-only "optimization" of NormalizeName: Arabic letters must
        // survive normalization, and internal-whitespace differences must not break MENA store matching.
        var model = WithOffers(new PriceOffer { Source = "مكتبة جرير" });
        var stores = new[] { Store("مكتبة  جرير", null, "خدمة ممتازة") };

        StoreReviewMatcher.ReviewsFor(model, stores)
            .Should().ContainSingle(r => r.Text == "خدمة ممتازة");
    }

    [Fact]
    public void SubdomainHost_DoesNotMatch_ByDesign()
    {
        // Deliberate miss: the domain fallback compares full hosts (only "www." is stripped), so an
        // offer on a subdomain never matches the apex-domain store. Misses are expected in this
        // best-effort join, and stripping subdomains would over-match unrelated tenants on shared
        // platforms. A future "improve match rate" change must trip this test consciously.
        var model = WithOffers(new PriceOffer { Source = "Offer Src", Url = "https://store.example.com/p/1" });
        var stores = new[] { Store("Example Shop", "https://example.com", "should not surface") };

        StoreReviewMatcher.ReviewsFor(model, stores).Should().BeEmpty();
    }
}
