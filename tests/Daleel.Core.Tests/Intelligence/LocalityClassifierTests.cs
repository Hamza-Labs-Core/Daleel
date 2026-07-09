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

/// <summary>
/// Pins the generalized geo-scoped generic-gTLD rule (the jo-cell case): real local stores live on
/// bare .com in every market, and when Google was queried WITH gl=cc it already constrained the
/// results — the classifier must not veto them for lacking a ccTLD.
/// </summary>
public class GeoScopedLocalityTests
{
    [Theory]
    [InlineData("https://jo-cell.com/collections/espresso-machines", "Espresso Machines - JO Cell Amman")]
    [InlineData("https://dumyah.com/en/home-and-kitchen", "Coffee & Tea Makers | Dumyah.com Jordan")]
    [InlineData("https://smartbuy-me.com/collections/coffee-maker", "Coffee Makers — SmartBuy Jordan online")]
    [InlineData("https://wholeandall.com/collections/coffee-makers", "Coffee makers and grinders, delivery across Jordan")]
    public void Geo_scoped_generic_gtlds_with_market_evidence_are_local(string url, string evidence) =>
        Assert.True(LocalityClassifier.IsLocal(
            url, "jo", "Jordan", fromGeoScopedSearch: true, marketEvidence: evidence));

    [Fact]
    public void Geo_scoped_without_market_evidence_stays_non_local()
    {
        // AliExpress ranks under gl=jo too — a generic snippet must not make a global seller "local".
        Assert.False(LocalityClassifier.IsLocal(
            "https://www.aliexpress.com/item/1", "jo", "Jordan",
            fromGeoScopedSearch: true, marketEvidence: "Espresso machine, free worldwide shipping"));
        Assert.False(LocalityClassifier.IsLocal(
            "https://global-store.com/product/imported-ac", "jo", "Jordan", fromGeoScopedSearch: true));
    }

    [Theory]
    [InlineData("https://www.amazon.de/dp/1")]          // foreign ccTLD — not a generic gTLD
    [InlineData("https://www.noon.com/uae-en/machine")] // foreign locale path on a generic gTLD
    public void Foreign_signals_still_veto_even_when_geo_scoped(string url) =>
        Assert.False(LocalityClassifier.IsLocal(
            url, "jo", "Jordan", fromGeoScopedSearch: true, marketEvidence: "buy in Jordan"));

    [Fact]
    public void Own_market_locale_paths_survive_the_foreign_veto() =>
        Assert.True(LocalityClassifier.IsLocal(
            "https://souqprice.com/jo/en-jo/product/search", "jo", "Jordan", fromGeoScopedSearch: true));

    [Fact]
    public void Host_name_country_segment_is_local_even_unscoped()
    {
        // A seller that put the market as a BOUNDED segment of its domain advertises its market.
        Assert.True(LocalityClassifier.IsLocal("https://jo-cell.com/products/x", "jo", "Jordan"));
        Assert.True(LocalityClassifier.IsLocal("https://cell-jo.com/x", "jo", "Jordan"));
        Assert.True(LocalityClassifier.IsLocal("https://jordan-mall.com/shop", "jo", "Jordan"));
    }

    [Theory]
    // Mid-word substrings are NOT the country: Nike Air Jordan, a US dentist named Jordan.
    [InlineData("https://airjordan.com/shoes")]
    [InlineData("https://jordandental.com/book")]
    public void Country_name_as_a_mid_word_substring_is_not_local(string url) =>
        Assert.False(LocalityClassifier.IsLocal(url, "jo", "Jordan"));

    [Fact]
    public void Unscoped_bare_com_without_any_signal_stays_non_local() =>
        Assert.False(LocalityClassifier.IsLocal("https://dumyah.com/en/home", "jo", "Jordan"));
}
