using Daleel.Web.Identification;
using FluentAssertions;
using Xunit;

namespace Daleel.Web.Tests.Identification;

/// <summary>Pins the regional candidate expansion: Jordan-first ordering, known-brand patterns, and the base domain always being probed.</summary>
public class BrandRegionsTests
{
    [Theory]
    [InlineData("https://www.samsung.com", "samsung.com")]
    [InlineData("samsung.com/jo", "samsung.com")]
    [InlineData("WWW.Samsung.COM", "samsung.com")]
    public void RootDomain_StripsSchemeWwwAndPath(string input, string expected)
    {
        BrandRegions.RootDomain(input).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void RootDomain_ReturnsNullForBlank(string? input)
    {
        BrandRegions.RootDomain(input).Should().BeNull();
    }

    [Fact]
    public void CandidatesFor_IsJordanFirst()
    {
        var candidates = BrandRegions.CandidatesFor("acme.com");
        candidates.Should().NotBeEmpty();
        candidates[0].Region.Key.Should().Be("jordan");
    }

    [Fact]
    public void CandidatesFor_UsesKnownBrandPatterns_ForSamsung()
    {
        var domains = BrandRegions.CandidatesFor("samsung.com").Select(c => c.Domain).ToList();

        domains.Should().Contain(d => d.Contains("samsung.com/levant") || d.Contains("samsung.com/jo"));
        domains.Should().Contain("samsung.com/ae");
    }

    [Fact]
    public void CandidatesFor_AlwaysIncludesTheBaseDomain()
    {
        BrandRegions.CandidatesFor("acme.com").Select(c => c.Domain).Should().Contain("acme.com");
    }

    [Fact]
    public void CandidatesFor_DeduplicatesAndCoversEveryRegion()
    {
        var candidates = BrandRegions.CandidatesFor("acme.com");
        var domains = candidates.Select(c => c.Domain).ToList();

        domains.Should().OnlyHaveUniqueItems();
        candidates.Select(c => c.Region.Key).Distinct().Should()
            .BeEquivalentTo(new[] { "jordan", "uae", "saudi", "egypt", "global" });
    }

    [Fact]
    public void CandidatesFor_ReturnsEmptyForUnusableDomain()
    {
        BrandRegions.CandidatesFor(null).Should().BeEmpty();
    }
}
