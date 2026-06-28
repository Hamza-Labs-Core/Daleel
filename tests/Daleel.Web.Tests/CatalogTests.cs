using System.Globalization;
using Daleel.Web.Services;
using FluentAssertions;
using Xunit;

namespace Daleel.Web.Tests;

public class CatalogTests
{
    [Theory]
    [InlineData("مكيف سبليت", true)]
    [InlineData("أفضل هاتف", true)]
    [InlineData("best phone", false)]
    [InlineData("iPhone 15", false)]
    [InlineData("", false)]
    [InlineData("Samsung جالاكسي", true)] // mixed → Arabic present
    public void IsArabic_DetectsArabicScript(string text, bool expected) =>
        Catalog.IsArabic(text).Should().Be(expected);

    [Theory]
    [InlineData("مرحبا", "rtl")]
    [InlineData("hello", "ltr")]
    public void Dir_FollowsScript(string text, string expected) =>
        Catalog.Dir(text).Should().Be(expected);

    [Fact]
    public void ResolveGeo_FallsBackToFirstForUnknownKey() =>
        Catalog.ResolveGeo("atlantis").Should().BeSameAs(Catalog.Geos[0]);

    [Fact]
    public void ResolveGeo_IsCaseInsensitive() =>
        Catalog.ResolveGeo("JORDAN").Key.Should().Be("jordan");

    [Fact]
    public void Catalogs_AreNonEmptyAndDefaultModelIsListed()
    {
        Catalog.Geos.Should().NotBeEmpty();
        Catalog.Models.Should().NotBeEmpty();
        Catalog.Models.Select(m => m.Id).Should().Contain(Catalog.DefaultModel);
    }

    [Fact]
    public void GeoOption_DisplayShowsEnglishNameUnderEnglishCulture()
    {
        using var _ = new CultureScope("en");
        var jordan = Catalog.ResolveGeo("jordan");
        jordan.Display.Should().Contain("🇯🇴").And.Contain("Jordan");
        // The English UI must NOT leak the Arabic name — that was the localization bug.
        jordan.Display.Should().NotContain("الأردن");
    }

    [Fact]
    public void GeoOption_DisplayShowsArabicNameUnderArabicCulture()
    {
        using var _ = new CultureScope("ar");
        var jordan = Catalog.ResolveGeo("jordan");
        jordan.Display.Should().Contain("🇯🇴").And.Contain("الأردن");
        jordan.Display.Should().NotContain("Jordan");
    }

    /// <summary>Pins <see cref="CultureInfo.CurrentUICulture"/> for the scope of one test, then restores it.</summary>
    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _ui;

        public CultureScope(string name)
        {
            _ui = CultureInfo.CurrentUICulture;
            CultureInfo.CurrentUICulture = new CultureInfo(name);
        }

        public void Dispose() => CultureInfo.CurrentUICulture = _ui;
    }
}
