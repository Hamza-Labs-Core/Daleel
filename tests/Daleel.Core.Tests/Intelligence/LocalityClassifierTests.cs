using Daleel.Core.Intelligence;
using FluentAssertions;
using Xunit;

namespace Daleel.Core.Tests.Intelligence;

public class LocalityClassifierTests
{
    [Fact]
    public void GeoTargetedSource_IsAlwaysLocal()
    {
        // Shopping/Places hits are geo-constrained by the search itself.
        LocalityClassifier.IsLocal("https://anywhere.com/x", "jo", "Jordan", fromGeoTargetedSource: true)
            .Should().BeTrue();
    }

    [Theory]
    [InlineData("https://store.com.jo/ac")]      // country TLD
    [InlineData("https://shop.jo/ac")]           // bare country TLD
    [InlineData("https://jo.example.com/ac")]    // country subdomain
    [InlineData("https://example.com/jo/ac")]    // country path segment
    [InlineData("https://example.com/en-jo/ac")] // locale path segment
    public void CountrySignals_AreLocal(string url)
    {
        LocalityClassifier.IsLocal(url, "jo", "Jordan").Should().BeTrue();
    }

    [Theory]
    [InlineData("https://www.amazon.ae/dp/x")]
    [InlineData("https://global.example.com/product")]
    [InlineData("https://noon.com/uae-en/x")]
    public void NonCountryDomains_AreNotLocal(string url)
    {
        LocalityClassifier.IsLocal(url, "jo", "Jordan").Should().BeFalse();
    }

    [Fact]
    public void IsGeoAgnostic_WorksForAnyCountryCode()
    {
        LocalityClassifier.IsLocal("https://jumia.com.eg/ac", "eg", "Egypt").Should().BeTrue();
        LocalityClassifier.IsLocal("https://jumia.com.eg/ac", "jo", "Jordan").Should().BeFalse();
    }

    [Theory]
    [InlineData("ACs in Jordan", false)]
    [InlineData("best AC show international options too", true)]
    [InlineData("مكيفات عالمي", true)]
    [InlineData("worldwide AC deals", true)]
    public void QueryWantsInternational_Detects(string query, bool expected)
    {
        LocalityClassifier.QueryWantsInternational(query).Should().Be(expected);
    }
}
