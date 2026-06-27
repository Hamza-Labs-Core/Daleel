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

    [Theory]
    [InlineData(31.95, 35.91, "jordan")]   // Amman
    [InlineData(24.71, 46.68, "saudi")]    // Riyadh
    [InlineData(25.20, 55.27, "uae")]      // Dubai
    [InlineData(30.04, 31.24, "egypt")]    // Cairo
    [InlineData(40.71, -74.01, "usa")]     // New York
    [InlineData(34.05, -118.24, "usa")]    // Los Angeles → nearest supported market is the USA
    [InlineData(51.51, -0.13, "egypt")]    // London → no cutoff; Cairo (~3500km) is the nearest of the supported markets
    public void NearestTo_MapsCoordinatesToNearestMarket(double lat, double lng, string expectedKey)
    {
        GeoProfiles.NearestTo(lat, lng)!.Key.Should().Be(expectedKey);
    }
}
