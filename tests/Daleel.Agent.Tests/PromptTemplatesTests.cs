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

    [Fact]
    public void ExtractProducts_AsksForBreadthAndPerModelProsConsVerdict()
    {
        var prompt = PromptTemplates.ExtractProducts("best ACs", GeoProfiles.Jordan, "context");

        // Push for many brands / multiple models so the grid isn't just the top one or two.
        prompt.Should().Contain("COMPREHENSIVE");
        prompt.Should().Contain("EVERY distinct model");
        // The per-model verdict fields the UI now surfaces.
        prompt.Should().Contain("\"pros\"");
        prompt.Should().Contain("\"cons\"");
        prompt.Should().Contain("\"summary\"");
    }

    [Fact]
    public void PlanProduct_ExpandsAbbreviationsAndPushesForBrandBreadth()
    {
        var prompt = PromptTemplates.PlanProduct("AC", GeoProfiles.Jordan);

        // Ambiguous two-letter queries must be expanded, not searched literally.
        prompt.Should().Contain("expand");
        prompt.Should().Contain("air conditioner");
        // Breadth: many brands and multiple models per brand.
        prompt.Should().Contain("MANY brands");
        // Several review/buying-guide queries feed the "related articles" section.
        prompt.Should().Contain("buying-guide");
    }
}
