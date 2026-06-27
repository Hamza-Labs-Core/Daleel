using Daleel.Core.Geo;
using FluentAssertions;
using Xunit;

namespace Daleel.Core.Tests.Geo;

/// <summary>
/// Detecting the market straight from the search query — the strongest signal of where the user is
/// shopping. Recognises country and city names in English and Arabic, ignores 2-letter codes (which
/// would false-match ordinary words), and returns the market named earliest when several appear.
/// </summary>
public class MarketFromQueryTests
{
    [Theory]
    [InlineData("best air conditioner in Dubai", "uae")]
    [InlineData("cheapest washing machine in amman", "jordan")]
    [InlineData("افضل مكيف في الرياض", "saudi")]
    [InlineData("غسالة في عمان", "jordan")]
    [InlineData("laptops in cairo", "egypt")]
    [InlineData("TV deals in New York", "usa")]
    [InlineData("أسعار الثلاجات في السعودية", "saudi")]
    public void DetectInText_FindsMarketFromCountryOrCity(string query, string expectedKey)
    {
        GeoProfiles.DetectInText(query)!.Key.Should().Be(expectedKey);
    }

    [Theory]
    [InlineData("best wireless earbuds under 100 JOD", "jordan")]
    [InlineData("phones below 2000 SAR", "saudi")]
    [InlineData("laptop deals around 3000 AED", "uae")]
    [InlineData("fridge for 15000 EGP", "egypt")]
    [InlineData("headphones under 50 USD", "usa")]
    public void DetectInText_FindsMarketFromCurrencyCode(string query, string expectedKey)
    {
        GeoProfiles.DetectInText(query)!.Key.Should().Be(expectedKey);
    }

    [Theory]
    [InlineData("best air conditioner")]            // no location
    [InlineData("please tell us the price")]        // "us" must NOT match USA
    [InlineData("a usable fridge")]                 // "usa" inside a word must NOT match
    [InlineData("jodhpur travel guide")]            // "jod" inside a word must NOT match Jordan
    [InlineData("")]
    [InlineData(null)]
    public void DetectInText_ReturnsNull_WhenNoMarketNamed(string? query)
    {
        GeoProfiles.DetectInText(query).Should().BeNull();
    }

    [Fact]
    public void DetectInText_PrefersTheMarketMentionedFirst()
    {
        // "Dubai" appears before "Amman" → UAE wins.
        GeoProfiles.DetectInText("compare prices in Dubai vs Amman")!.Key.Should().Be("uae");
    }
}
