using Daleel.Web.Pipeline;
using FluentAssertions;
using Xunit;

namespace Daleel.Web.Tests.Pipeline;

/// <summary>
/// Parsing rules for the Cloudflare-fallback price extractor: it must recognize prices written
/// symbol-first ($1,299) and code-first (450 JOD), across the app's MENA + Western currencies, while
/// keeping the line so the caller can token-match a price to a specific product. The negative cases
/// guard against treating bare numbers (model numbers, spec values) as prices.
/// </summary>
public class PriceParserTests
{
    [Theory]
    [InlineData("Samsung TV — $1,299.00", 1299.00, "USD")]
    [InlineData("Price: JOD 450", 450, "JOD")]
    [InlineData("450 JOD only", 450, "JOD")]
    [InlineData("Now SAR 2999.99", 2999.99, "SAR")]
    [InlineData("€899 incl. VAT", 899, "EUR")]
    [InlineData("1299 USD", 1299, "USD")]
    public void Extract_ReadsPriceAndCurrency(string line, double expected, string currency)
    {
        var matches = PriceParser.Extract(line);

        matches.Should().ContainSingle();
        matches[0].Price.Should().Be((decimal)expected);
        matches[0].Currency.Should().Be(currency);
        matches[0].Line.Should().Be(line);
    }

    [Theory]
    [InlineData("Model number SM-S928 with 12GB RAM")]
    [InlineData("Free shipping on orders")]
    [InlineData("")]
    [InlineData("Rated 4.5 stars by 1200 reviews")]
    public void Extract_IgnoresNumbersWithoutCurrency(string text)
    {
        PriceParser.Extract(text).Should().BeEmpty();
    }

    [Fact]
    public void Extract_FindsMultiplePricesAcrossLines()
    {
        var text = "Galaxy S24 — $999\nGalaxy A55 — $399\nNo price here\nGalaxy Z Fold — $1,799.99";

        var matches = PriceParser.Extract(text);

        matches.Should().HaveCount(3);
        matches.Select(m => m.Price).Should().Equal(999m, 399m, 1799.99m);
    }

    [Fact]
    public void Extract_RespectsMaxResults()
    {
        var text = string.Join('\n', Enumerable.Range(0, 50).Select(i => $"Item {i} — $9{i}"));

        PriceParser.Extract(text, maxResults: 10).Should().HaveCount(10);
    }

    [Fact]
    public void Extract_NormalizesJdAliasToJod()
    {
        PriceParser.Extract("Special offer JD 79.50")[0].Currency.Should().Be("JOD");
    }
}
