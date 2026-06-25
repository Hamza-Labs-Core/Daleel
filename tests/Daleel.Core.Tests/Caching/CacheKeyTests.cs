using Daleel.Core.Caching;
using FluentAssertions;
using Xunit;

namespace Daleel.Core.Tests.Caching;

public class CacheKeyTests
{
    [Fact]
    public void ForProvider_CollapsesCosmeticQueryDifferences()
    {
        var a = CacheKey.ForProvider("SerpApi", "Web", "iPhone 15", "JO", "ar", null, 10);
        var b = CacheKey.ForProvider("serpapi", "web", "  iphone   15 ", "jo", "AR", null, 10);

        a.Should().Be(b);
    }

    [Fact]
    public void ForProvider_DiffersByGeo()
    {
        var jo = CacheKey.ForProvider("serpapi", "web", "laptop", "jo", "en", null, 10);
        var sa = CacheKey.ForProvider("serpapi", "web", "laptop", "sa", "en", null, 10);

        jo.Should().NotBe(sa);
    }

    [Fact]
    public void ForProvider_DiffersByProviderKindAndMaxResults()
    {
        var baseKey = CacheKey.ForProvider("serpapi", "web", "tv", "jo", "en", null, 10);

        CacheKey.ForProvider("bing", "web", "tv", "jo", "en", null, 10).Should().NotBe(baseKey);
        CacheKey.ForProvider("serpapi", "shopping", "tv", "jo", "en", null, 10).Should().NotBe(baseKey);
        CacheKey.ForProvider("serpapi", "web", "tv", "jo", "en", null, 20).Should().NotBe(baseKey);
    }

    [Fact]
    public void ForResult_CollapsesCosmeticDifferences_ButSeparatesGeo()
    {
        CacheKey.ForResult("Best Laptops", "jordan", "en")
            .Should().Be(CacheKey.ForResult("  best   laptops ", "JORDAN", "EN"));

        CacheKey.ForResult("best laptops", "jordan", "en")
            .Should().NotBe(CacheKey.ForResult("best laptops", "ksa", "en"));
    }

    [Fact]
    public void Keys_AreLayerPrefixed()
    {
        var provider = CacheKey.ForProvider("serpapi", "web", "x", "jo", "en", null, 10);
        var result = CacheKey.ForResult("x", "jo", "en");

        provider.Should().StartWith("provider:");
        result.Should().StartWith("result:");
        CacheKey.LayerOf(provider).Should().Be(CacheKey.ProviderLayer);
        CacheKey.LayerOf(result).Should().Be(CacheKey.ResultLayer);
    }

    [Fact]
    public void Normalize_FoldsArabicOrthographicVariants()
    {
        // أ (alef-with-hamza) folds to ا (bare alef) — the same canonical query.
        CacheKey.Normalize("أحمد").Should().Be(CacheKey.Normalize("احمد"));
    }
}
