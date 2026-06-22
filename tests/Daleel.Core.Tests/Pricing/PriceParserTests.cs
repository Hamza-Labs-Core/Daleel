using Daleel.Core.Pricing;
using FluentAssertions;
using Xunit;

namespace Daleel.Core.Tests.Pricing;

public class PriceParserTests
{
    [Theory]
    [InlineData("$1,299.00", 1299.00, "USD")]
    [InlineData("USD 450", 450, "USD")]
    [InlineData("JOD 1299.500", 1299.500, "JOD")]
    [InlineData("450 JD", 450, "JOD")]
    [InlineData("2,500 AED", 2500, "AED")]
    [InlineData("€999", 999, "EUR")]
    public void TryParse_ParsesCommonFormats(string text, decimal amount, string currency)
    {
        PriceParser.TryParse(text, out var money).Should().BeTrue();
        money.Amount.Should().Be(amount);
        money.Currency.Should().Be(currency);
    }

    [Fact]
    public void TryParse_HandlesArabicCurrencyWord()
    {
        PriceParser.TryParse("السعر 450 دينار", out var money).Should().BeTrue();
        money.Amount.Should().Be(450);
        money.Currency.Should().Be("JOD");
    }

    [Fact]
    public void TryParse_FoldsArabicIndicDigits()
    {
        // ٤٥٠ = 450
        PriceParser.TryParse("٤٥٠ دينار", out var money).Should().BeTrue();
        money.Amount.Should().Be(450);
        money.Currency.Should().Be("JOD");
    }

    [Fact]
    public void TryParse_EuropeanDecimalComma()
    {
        // "1.299,50" → 1299.50
        PriceParser.TryParse("1.299,50 EUR", out var money).Should().BeTrue();
        money.Amount.Should().Be(1299.50m);
        money.Currency.Should().Be("EUR");
    }

    [Fact]
    public void TryParse_ThousandsCommaNoDecimal()
    {
        PriceParser.TryParse("$1,299", out var money).Should().BeTrue();
        money.Amount.Should().Be(1299);
    }

    [Fact]
    public void TryParse_UsesDefaultCurrencyWhenNoneDetected()
    {
        PriceParser.TryParse("1500", out var money, defaultCurrency: "JOD").Should().BeTrue();
        money.Amount.Should().Be(1500);
        money.Currency.Should().Be("JOD");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no numbers here")]
    public void TryParse_FailsOnNonPrice(string? text)
    {
        PriceParser.TryParse(text, out _).Should().BeFalse();
    }

    [Fact]
    public void Parse_ThrowsOnFailure()
    {
        var act = () => PriceParser.Parse("nothing");
        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void DetectsLongestCurrencyToken()
    {
        // "دينار اردني" should resolve to JOD even though "دينار" alone also matches.
        PriceParser.TryParse("500 دينار اردني", out var money).Should().BeTrue();
        money.Currency.Should().Be("JOD");
    }
}
