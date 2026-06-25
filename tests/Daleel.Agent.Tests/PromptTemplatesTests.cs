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

    [Fact]
    public void ExtractProducts_AsksForStructuredProductsWithOffersAndEmbedsContext()
    {
        var prompt = PromptTemplates.ExtractProducts("best ACs", GeoProfiles.Jordan, "Samsung 320 JOD at Smart Buy");

        // The schema the extractor depends on: products → offers (price/currency/url).
        prompt.Should().Contain("\"products\"");
        prompt.Should().Contain("\"offers\"");
        prompt.Should().Contain("\"price\"");
        // The gathered context is embedded so the LLM extracts from real data, not memory.
        prompt.Should().Contain("Samsung 320 JOD at Smart Buy");
        // Market currency is surfaced so bare prices are interpreted correctly.
        prompt.Should().Contain(GeoProfiles.Jordan.Currency);
        // Anti-hallucination guard.
        prompt.Should().Contain("never invent");
    }
}
