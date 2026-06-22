using Daleel.Core.Llm;
using FluentAssertions;
using Xunit;

namespace Daleel.Core.Tests.Llm;

public class LlmJsonTests
{
    private sealed class Sample
    {
        public string? Name { get; set; }
        public int Count { get; set; }
    }

    [Fact]
    public void ExtractJson_PlainObject()
    {
        LlmJson.ExtractJson("""{"name":"x","count":3}""")
            .Should().Be("""{"name":"x","count":3}""");
    }

    [Fact]
    public void ExtractJson_StripsMarkdownFence()
    {
        var text = "```json\n{\"name\":\"x\"}\n```";
        LlmJson.ExtractJson(text).Should().Be("{\"name\":\"x\"}");
    }

    [Fact]
    public void ExtractJson_IgnoresSurroundingProse()
    {
        var text = "Here is your result:\n{\"name\":\"y\"}\nHope that helps!";
        LlmJson.ExtractJson(text).Should().Be("{\"name\":\"y\"}");
    }

    [Fact]
    public void ExtractJson_HandlesNestedBraces()
    {
        var text = """{"a":{"b":1},"c":[1,2]}""";
        LlmJson.ExtractJson(text).Should().Be(text);
    }

    [Fact]
    public void ExtractJson_HandlesBracesInsideStrings()
    {
        var text = """{"note":"a } brace in a string"}""";
        LlmJson.ExtractJson(text).Should().Be(text);
    }

    [Fact]
    public void ExtractJson_ExtractsArray()
    {
        LlmJson.ExtractJson("prefix [1,2,3] suffix").Should().Be("[1,2,3]");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no json at all")]
    public void ExtractJson_ReturnsNullWhenAbsent(string? text)
    {
        LlmJson.ExtractJson(text).Should().BeNull();
    }

    [Fact]
    public void Deserialize_RoundTripsThroughFence()
    {
        var text = "```json\n{\"name\":\"widget\",\"count\":7}\n```";
        var sample = LlmJson.Deserialize<Sample>(text);
        sample.Should().NotBeNull();
        sample!.Name.Should().Be("widget");
        sample.Count.Should().Be(7);
    }

    [Fact]
    public void Deserialize_ReturnsDefaultOnGarbage()
    {
        LlmJson.Deserialize<Sample>("not json").Should().BeNull();
    }
}
