using Daleel.Core.Intelligence;
using FluentAssertions;
using Xunit;

namespace Daleel.Core.Tests.Intelligence;

public class ResultClassifierTests
{
    [Fact]
    public void BrandDomain_IsBrandPage()
    {
        ResultClassifier.Classify("https://www.samsung.com/jo/air-conditioners/", "Air Conditioners | Samsung Jordan", "")
            .Should().Be(ResultType.BrandPage);

        ResultClassifier.Classify("https://www.lg.com/levant/air-conditioners", "LG Air Conditioners", "")
            .Should().Be(ResultType.BrandPage);
    }

    [Fact]
    public void MarketplaceCategoryPage_IsMarketplace()
    {
        ResultClassifier.Classify("https://jo.opensooq.com/en/air-conditioners", "Air Conditioners for sale in Jordan", "Browse ads")
            .Should().Be(ResultType.Marketplace);
    }

    [Fact]
    public void MarketplaceItemWithPrice_IsProductListing()
    {
        ResultClassifier.Classify(
            "https://jo.opensooq.com/en/listing/12345678",
            "Samsung Split AC AR24",
            "Brand new, 450 JOD, Amman")
            .Should().Be(ResultType.ProductListing);
    }

    [Fact]
    public void StoreSection_IsStorePage_ButPricedIsListing()
    {
        ResultClassifier.Classify("https://www.carrefourjordan.com/electronics/air-conditioners", "Air Conditioners - Carrefour", "")
            .Should().Be(ResultType.StorePage);

        ResultClassifier.Classify("https://www.carrefourjordan.com/p/midea-ac", "Midea AC", "السعر 399 دينار")
            .Should().Be(ResultType.ProductListing);
    }

    [Theory]
    [InlineData("Best ACs in Jordan 2024 - Buyer's Guide")]
    [InlineData("Samsung vs LG air conditioners: which to buy?")]
    [InlineData("أفضل مكيفات في الأردن")]
    public void GuideAndReviewTitles_AreReviewArticles(string title)
    {
        ResultClassifier.Classify("https://blog.example.com/article", title, "")
            .Should().Be(ResultType.ReviewArticle);
    }

    [Fact]
    public void GenericPagewithPrice_IsProductListing()
    {
        ResultClassifier.Classify("https://shop.example.com/x", "Cooling unit", "Now only 299 AED")
            .Should().Be(ResultType.ProductListing);
    }

    [Fact]
    public void BareUnknownDomain_IsBrandPageFallback()
    {
        ResultClassifier.Classify("https://coolair.com", "CoolAir", "Home")
            .Should().Be(ResultType.BrandPage);
    }

    [Fact]
    public void NoSignals_IsUnknown()
    {
        ResultClassifier.Classify("https://news.example.com/world/politics/article-about-something", "Weather today", "It is sunny")
            .Should().Be(ResultType.Unknown);
    }

    [Theory]
    [InlineData("ProductListing", ResultType.ProductListing)]
    [InlineData("brand page", ResultType.BrandPage)]
    [InlineData("STORE", ResultType.StorePage)]
    [InlineData("a review article", ResultType.ReviewArticle)]
    [InlineData("Marketplace", ResultType.Marketplace)]
    [InlineData("nonsense", ResultType.Unknown)]
    public void Parse_MapsLabels(string label, ResultType expected)
    {
        ResultClassifier.Parse(label).Should().Be(expected);
    }
}
