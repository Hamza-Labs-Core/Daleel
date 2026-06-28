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
    // The US's de-facto local commerce namespace is the bare generic gTLD — US retailers live on
    // .com/.net/.store, never a ".us" ccTLD or "/us/" path. These MUST be local for the US market,
    // or every US seller is dropped and a "best carry on USA" search returns no products/brands.
    [InlineData("https://www.amazon.com/dp/x")]
    [InlineData("https://www.walmart.com/ip/x")]
    [InlineData("https://www.rei.com/product/x")]
    [InlineData("https://www.away.com/luggage/carry-on")]
    [InlineData("https://shop.monos.com/products/carry-on")]
    public void GenericGTlds_AreLocalForUs(string url)
    {
        LocalityClassifier.IsLocal(url, "us", "United States").Should().BeTrue();
    }

    [Theory]
    // A genuinely foreign seller must still be dropped for a US shopper — a non-US ccTLD or another
    // country's locale path is the signal, even though the US otherwise accepts bare gTLDs.
    [InlineData("https://www.amazon.ae/dp/x")]      // UAE ccTLD
    [InlineData("https://www.amazon.co.uk/dp/x")]   // UK ccTLD
    [InlineData("https://noon.com/uae-en/x")]       // foreign locale path
    public void ForeignSellers_AreNotLocalForUs(string url)
    {
        LocalityClassifier.IsLocal(url, "us", "United States").Should().BeFalse();
    }

    [Fact]
    public void GenericGTlds_StayNonLocalForOtherMarkets()
    {
        // The gTLD-is-local rule is US-only: an Arabic-first market still drops international .com
        // so its results stay genuinely local (the app's primary purpose).
        LocalityClassifier.IsLocal("https://www.amazon.com/dp/x", "jo", "Jordan").Should().BeFalse();
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
