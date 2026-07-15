using Daleel.Web.Services;
using FluentAssertions;
using Xunit;

namespace Daleel.Web.Tests.Services;

/// <summary>
/// The stock-status classifier behind the card chip. Availability strings arrive from LLM
/// extraction in every store's own wording (English, Arabic, snake_case, prose) — the classifier
/// folds them into the three states the UI can act on, and Unknown for everything else (no chip).
/// </summary>
public class StockStatusTests
{
    [Theory]
    [InlineData("in stock")]
    [InlineData("In Stock")]
    [InlineData("instock")]
    [InlineData("available")]
    [InlineData("متوفر")]
    [InlineData("متاح")]
    public void InStockVariants(string s) => StockStatus.Classify(s).Should().Be(Stock.InStock);

    [Theory]
    [InlineData("out of stock")]
    [InlineData("OutOfStock")]
    [InlineData("sold out")]
    [InlineData("unavailable")]
    [InlineData("غير متوفر")]
    [InlineData("نفذ من المخزون")]
    public void OutOfStockVariants(string s) => StockStatus.Classify(s).Should().Be(Stock.OutOfStock);

    [Theory]
    [InlineData("preorder")]
    [InlineData("pre-order")]
    [InlineData("الطلب المسبق")]
    public void PreorderVariants(string s) => StockStatus.Classify(s).Should().Be(Stock.Preorder);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ships from warehouse")]
    public void UnknownOtherwise(string? s) => StockStatus.Classify(s).Should().Be(Stock.Unknown);

    [Fact]
    public void OutOfStock_WinsOverContainedInStock()
    {
        // "out of stock" CONTAINS "stock"; the negative phrasing must be checked first.
        StockStatus.Classify("Out of stock").Should().Be(Stock.OutOfStock);
    }
}
