using Daleel.Agent;
using Daleel.Core.Geo;
using FluentAssertions;
using Xunit;

namespace Daleel.Agent.Tests;

public class PromptTemplatesTests
{
    [Theory]
    [InlineData("ar", "Arabic")]
    [InlineData("en", "English")]
    [InlineData(null, "English")]
    public void LanguageName_Maps(string? code, string expected)
    {
        PromptTemplates.LanguageName(code).Should().Be(expected);
    }

    [Fact]
    public void Analyze_IncludesRespondInLanguageDirective()
    {
        var prompt = PromptTemplates.Analyze("best AC", GeoProfiles.Jordan, "some context", "ar");
        prompt.Should().Contain("Respond in Arabic");
        // Brand/model names must be preserved, not translated.
        prompt.Should().Contain("do not translate");
    }

    [Fact]
    public void Analyze_DefaultsToEnglish()
    {
        PromptTemplates.Analyze("x", GeoProfiles.Jordan, "ctx").Should().Contain("Respond in English");
    }
}
