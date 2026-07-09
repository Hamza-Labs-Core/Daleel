using Daleel.Web.Profiles;
using FluentAssertions;
using Xunit;

namespace Daleel.Web.Tests.Profiles;

/// <summary>
/// The name → hostname heuristic decides whether we spend money on a scrape. Every case it gets wrong is
/// a paid provider call to a host that does not exist (the scraper bills the same to answer "DNS
/// resolution failed" as it does to return a page), so the guard belongs here, before the call.
/// </summary>
public class GuessDomainTests
{
    [Theory]
    [InlineData("Leaders Center", "leaderscenter.com")]
    [InlineData("Smart-Buy!", "smartbuy.com")]
    [InlineData("Xmart 24", "xmart24.com")]
    public void GuessDomain_LatinName_SlugsToADotComHost(string name, string expected) =>
        ContextDevProfileResearcher.GuessDomain(name).Should().Be(expected);

    [Theory]
    // char.IsLetterOrDigit is TRUE for Arabic script, so these used to become hostnames like
    // "كارفورماركت.com" — invented hosts we then paid Context.dev to resolve. A hostname label is ASCII.
    [InlineData("كارفور ماركت")]
    [InlineData("هايبر ماكس")]
    [InlineData("المختار مول")]
    [InlineData("سوبرماركت")]
    public void GuessDomain_NonLatinName_IsNotGuessable(string arabicName) =>
        ContextDevProfileResearcher.GuessDomain(arabicName).Should().BeNull();

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!!")]
    [InlineData("ab")] // too short to be a plausible host
    public void GuessDomain_NothingToGuessFrom_ReturnsNullNeverExampleCom(string name)
    {
        var guessed = ContextDevProfileResearcher.GuessDomain(name);

        guessed.Should().BeNull();
        // The old fallback scraped the IANA example domain — a paid call that could never yield anything.
        guessed.Should().NotBe("example.com");
    }

    [Fact]
    public void GuessDomain_MixedScript_KeepsOnlyTheLatinPart()
    {
        // A store named "Carrefour كارفور" is still guessable from its Latin half.
        ContextDevProfileResearcher.GuessDomain("Carrefour كارفور").Should().Be("carrefour.com");
    }
}
