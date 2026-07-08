using Daleel.Web.Services;
using FluentAssertions;
using Xunit;

namespace Daleel.Web.Tests;

public class OpenRouterCatalogTests
{
    [Fact]
    public void Parse_ExtractsProviderNamePricingAndContext()
    {
        const string json = """
        { "data": [
            { "id": "anthropic/claude-sonnet-5", "name": "Anthropic: Claude Sonnet 5",
              "pricing": { "prompt": "0.000003", "completion": "0.000015" }, "context_length": 200000 },
            { "id": "openai/gpt-4o-mini", "name": "OpenAI: GPT-4o mini",
              "pricing": { "prompt": "0.00000015", "completion": "0.0000006" }, "context_length": 128000 }
        ] }
        """;

        var models = OpenRouterCatalog.Parse(json);

        models.Should().HaveCount(2);
        var sonnet = models.Single(m => m.Id == "anthropic/claude-sonnet-5");
        sonnet.Provider.Should().Be("anthropic");
        sonnet.Name.Should().Be("Anthropic: Claude Sonnet 5");
        sonnet.PromptPerMTok.Should().Be(3m);       // 0.000003 USD/token → $3 per Mtok
        sonnet.CompletionPerMTok.Should().Be(15m);
        sonnet.ContextLength.Should().Be(200000);
    }

    [Fact]
    public void Parse_SkipsEntriesWithoutId_AndPricelessModelsSurviveWithNulls()
    {
        const string json = """
        { "data": [ { "name": "no id here" }, { "id": "x/y" } ] }
        """;

        var models = OpenRouterCatalog.Parse(json);

        models.Should().ContainSingle();
        models[0].Id.Should().Be("x/y");
        models[0].Provider.Should().Be("x");
        models[0].PromptPerMTok.Should().BeNull();
        models[0].ContextLength.Should().BeNull();
    }

    [Fact]
    public void Parse_MalformedOrMissingData_ReturnsEmpty()
    {
        OpenRouterCatalog.Parse("{}").Should().BeEmpty();
        OpenRouterCatalog.Parse("""{ "data": "not-an-array" }""").Should().BeEmpty();
    }

    [Fact]
    public void Parse_SortsByProviderThenId()
    {
        const string json = """
        { "data": [
            { "id": "openai/gpt-4o" }, { "id": "anthropic/claude-opus-4.8" }, { "id": "anthropic/claude-sonnet-5" }
        ] }
        """;

        var models = OpenRouterCatalog.Parse(json);

        models.Select(m => m.Id).Should().ContainInOrder(
            "anthropic/claude-opus-4.8", "anthropic/claude-sonnet-5", "openai/gpt-4o");
    }
}
