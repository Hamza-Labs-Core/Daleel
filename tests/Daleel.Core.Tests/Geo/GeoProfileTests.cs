using Daleel.Core.Geo;
using FluentAssertions;
using Xunit;

namespace Daleel.Core.Tests.Geo;

public class GeoProfileTests
{
    [Theory]
    [InlineData("jordan")]
    [InlineData("Jordan")]
    [InlineData("JO")]
    [InlineData("jo")]
    [InlineData("Amman")]
    public void Resolve_FindsJordanByKeyCodeOrCity(string needle)
    {
        var profile = GeoProfiles.Resolve(needle);
        profile.Should().NotBeNull();
        profile!.Key.Should().Be("jordan");
    }

    [Fact]
    public void Resolve_ReturnsNullForUnknown()
    {
        GeoProfiles.Resolve("atlantis").Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Resolve_HandlesNullEmpty(string? input)
    {
        GeoProfiles.Resolve(input).Should().BeNull();
    }

    [Fact]
    public void ResolveOrDefault_FallsBackToUsa()
    {
        GeoProfiles.ResolveOrDefault("nowhere").Key.Should().Be("usa");
    }

    [Fact]
    public void Jordan_IsArabicFirst()
    {
        GeoProfiles.Jordan.IsArabicFirst.Should().BeTrue();
        GeoProfiles.Jordan.PrimaryLanguage.Should().Be("ar");
        GeoProfiles.Jordan.Currency.Should().Be("JOD");
    }

    [Fact]
    public void Usa_IsEnglishFirst()
    {
        GeoProfiles.Usa.IsArabicFirst.Should().BeFalse();
        GeoProfiles.Usa.PrimaryLanguage.Should().Be("en");
    }

    [Fact]
    public void All_ContainsThePreBuiltMarkets()
    {
        GeoProfiles.All.Select(p => p.Key)
            .Should().Contain(new[] { "jordan", "saudi", "uae", "egypt", "usa" });
    }

    [Fact]
    public void Jordan_HasOpenSooqMarketplace()
    {
        GeoProfiles.Jordan.Marketplaces.Should().Contain(m => m.Contains("opensooq"));
    }

    [Fact]
    public void Jordan_CenterIsAmman()
    {
        GeoProfiles.Jordan.CenterCity.Should().Be("Amman");
        GeoProfiles.Jordan.Center.Latitude.Should().BeApproximately(31.95, 0.1);
    }

    // The market is decided ONLY by (1) the query text and (2) a user-shared location that actually
    // places them INSIDE a supported market; anything else must return null so the UI asks. A shared
    // location outside every market (Berlin, London, Doha) must never silently map to the "nearest".
    [Theory]
    [InlineData(31.95, 35.91, "jordan")]   // Amman
    [InlineData(29.53, 35.00, "jordan")]   // Aqaba — Jordan's south-western corner (second box)
    [InlineData(24.71, 46.68, "saudi")]    // Riyadh
    [InlineData(21.49, 39.19, "saudi")]    // Jeddah — far from Riyadh but inside the kingdom
    [InlineData(25.20, 55.27, "uae")]      // Dubai
    [InlineData(30.04, 31.24, "egypt")]    // Cairo
    [InlineData(40.71, -74.01, "usa")]     // New York
    [InlineData(34.05, -118.24, "usa")]    // Los Angeles
    public void MarketContaining_ResolvesLocationsInsideAMarket(double lat, double lng, string expectedKey)
    {
        GeoProfiles.MarketContaining(lat, lng)!.Key.Should().Be(expectedKey);
    }

    [Theory]
    [InlineData(51.51, -0.13)]    // London — no market contains it; the UI must ask
    [InlineData(52.52, 13.40)]    // Berlin
    [InlineData(35.68, 139.69)]   // Tokyo
    [InlineData(25.28, 51.53)]    // Doha — inside Saudi's coarse box but Qatar is not a market
    [InlineData(29.37, 47.97)]    // Kuwait City — same
    [InlineData(31.78, 35.21)]    // Jerusalem — west of Jordan's main-body box
    public void MarketContaining_ReturnsNullOutsideSupportedMarkets(double lat, double lng)
    {
        GeoProfiles.MarketContaining(lat, lng).Should().BeNull();
    }
}
