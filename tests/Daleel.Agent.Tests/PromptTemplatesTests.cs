using Daleel.Agent;
using Daleel.Core.Geo;
using Daleel.Core.Models;
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
    public void ExtractProducts_AsksForBoundedBreadthAndPerModelProsConsVerdict()
    {
        var prompt = PromptTemplates.ExtractProducts("best ACs", GeoProfiles.Jordan, "context");

        // Push for many brands / multiple models so the grid isn't just the top one or two — but
        // BOUNDED: the old "extract EVERY distinct model" instruction produced multi-minute LLM
        // generations on article-heavy queries that blew the run's 10-minute budget, so the prompt
        // now caps extraction at the ~20 best-evidenced models.
        prompt.Should().Contain("~20 BEST-EVIDENCED");
        prompt.Should().Contain("MULTIPLE brands");
        prompt.Should().NotContain("EVERY distinct model");
        // The per-model verdict fields the UI now surfaces.
        prompt.Should().Contain("\"pros\"");
        prompt.Should().Contain("\"cons\"");
        prompt.Should().Contain("\"summary\"");
    }

    [Fact]
    public void StrategySchema_AsksPlannerToClassifyIntent()
    {
        var prompt = PromptTemplates.PlanFreeform("plumber in Amman", GeoProfiles.Jordan);

        // The planner must emit an intent and be told what the three values mean.
        prompt.Should().Contain("\"intent\"");
        prompt.Should().Contain("Product|Service|Place");
        prompt.Should().Contain("Service");
        prompt.Should().Contain("Place");
    }

    [Fact]
    public void ExtractProducts_ServiceIntent_AsksForProvidersTiersAndContact()
    {
        var prompt = PromptTemplates.ExtractProducts(
            "plumber in Amman", GeoProfiles.Jordan, "Aqua Plumbing 24/7 callout 25 JOD",
            intent: SearchIntentType.Service);

        // Service-shaped guidance: providers, pricing tiers, availability/contact — same JSON shape.
        prompt.Should().Contain("PROVIDERS");
        prompt.Should().Contain("pricing tier");
        prompt.Should().Contain("availability");
        prompt.Should().Contain("\"products\"");
        prompt.Should().Contain("Aqua Plumbing 24/7 callout 25 JOD");
        prompt.Should().Contain("never invent");
    }

    [Fact]
    public void ExtractProducts_PlaceIntent_AsksForVenuesHoursAddressAndMap()
    {
        var prompt = PromptTemplates.ExtractProducts(
            "best shawarma in Amman", GeoProfiles.Jordan, "Reem 4.7 stars, Abdoun",
            intent: SearchIntentType.Place);

        // Place-shaped guidance: venues with hours/address/map, offers normally empty.
        prompt.Should().Contain("PLACES");
        prompt.Should().Contain("hours");
        prompt.Should().Contain("address");
        prompt.Should().Contain("mapUrl");
        prompt.Should().Contain("Reem 4.7 stars, Abdoun");
    }

    [Theory]
    [InlineData(SearchIntentType.Product)]
    [InlineData(SearchIntentType.Service)]
    [InlineData(SearchIntentType.Place)]
    public void ExtractionSystem_PicksAPromptForEveryIntent(SearchIntentType intent)
    {
        var system = PromptTemplates.ExtractionSystem(intent);
        system.Should().NotBeNullOrWhiteSpace();
        // Every extraction system prompt enforces JSON-only structured output.
        system.Should().Contain("single JSON object only");
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
