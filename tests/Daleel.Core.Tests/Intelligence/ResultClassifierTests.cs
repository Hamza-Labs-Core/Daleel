using Daleel.Core.Intelligence;
using FluentAssertions;
using Xunit;

namespace Daleel.Core.Tests.Intelligence;

public class ResultClassifierTests
{
    [Fact]
    public void Price_AnywhereMeansProductListing()
    {
        ResultClassifier.Classify("https://anything.example/x", "Samsung Split AC", "Brand new, 450 JOD")
            .Should().Be(ResultType.ProductListing);
        ResultClassifier.Classify("https://shop.example.com/x", "Cooling unit", "Now only 299 AED")
            .Should().Be(ResultType.ProductListing);
    }

    [Theory]
    [InlineData("https://m.site/en/listing/12345678")]
    [InlineData("https://m.site/product/midea-ac")]
    [InlineData("https://m.site/dp/B0ABCXYZ")]
    public void ItemStylePath_IsProductListing(string url)
    {
        ResultClassifier.Classify(url, "Some AC", "no price here").Should().Be(ResultType.ProductListing);
    }

    [Fact]
    public void ClassifiedsWording_IsMarketplace()
    {
        ResultClassifier.Classify("https://m.example/en/air-conditioners", "Air Conditioners for sale in Jordan", "Browse ads")
            .Should().Be(ResultType.Marketplace);
    }

    [Theory]
    [InlineData("https://store.example/category/air-conditioners")]
    [InlineData("https://store.example/shop/cooling")]
    [InlineData("https://store.example/collections/ac")]
    public void ShopOrCategoryPath_IsStorePage(string url)
    {
        ResultClassifier.Classify(url, "Air Conditioners", "").Should().Be(ResultType.StorePage);
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
    public void PlainCommercialUrl_FallsBackToBrandPage()
    {
        // No price, no item/store path, no classifieds wording → treated generically as a brand page.
        ResultClassifier.Classify("https://www.samsung.com/jo/air-conditioners/", "Air Conditioners | Samsung Jordan", "")
            .Should().Be(ResultType.BrandPage);
        ResultClassifier.Classify("https://coolair.com", "CoolAir", "Home")
            .Should().Be(ResultType.BrandPage);
    }

    [Fact]
    public void NoUrl_IsUnknown_ForLlmFallback()
    {
        ResultClassifier.Classify(null, "Weather today", "It is sunny").Should().Be(ResultType.Unknown);
    }

    [Fact]
    public void IsGeoAgnostic_DoesNotDependOnSpecificDomains()
    {
        // The same structural signal classifies the same way regardless of which country/store it is.
        var jordan = ResultClassifier.Classify("https://shop.jo/category/ac", "ACs", "");
        var egypt = ResultClassifier.Classify("https://shop.eg/category/ac", "ACs", "");
        jordan.Should().Be(egypt).And.Be(ResultType.StorePage);
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
