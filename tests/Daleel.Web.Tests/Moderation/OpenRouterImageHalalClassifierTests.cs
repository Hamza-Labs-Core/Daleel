using Daleel.Web.Moderation;
using FluentAssertions;
using Xunit;

namespace Daleel.Web.Tests.Moderation;

public class OpenRouterImageHalalClassifierTests
{
    private static readonly IReadOnlyList<string> Batch = new[]
    {
        "https://img.example/a.jpg",
        "https://img.example/b.jpg",
        "https://img.example/c.jpg"
    };

    private static string Wrap(string content) =>
        "{\"choices\": [{\"message\": {\"content\": "
        + System.Text.Json.JsonSerializer.Serialize(content)
        + "}}]}";

    [Fact]
    public void Parse_MapsIndexedFlagsToUrls()
    {
        var body = Wrap("""[{"index": 1, "category": "alcohol", "confidence": 0.88, "reason": "wine bottles"}]""");

        var flagged = OpenRouterImageHalalClassifier.Parse(body, Batch);

        flagged.Should().NotBeNull();
        var verdict = flagged!.Should().ContainSingle().Subject.Value;
        verdict.ImageUrl.Should().Be("https://img.example/b.jpg");
        verdict.Category.Should().Be("alcohol");
        verdict.Confidence.Should().Be(0.88);
        verdict.IsHaram.Should().BeTrue();
    }

    [Fact]
    public void Parse_EmptyArray_MeansAllClean()
    {
        var flagged = OpenRouterImageHalalClassifier.Parse(Wrap("[]"), Batch);

        flagged.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void Parse_DiscardsOutOfRangeIndexesAndBadCategories()
    {
        var body = Wrap("""
            [
              {"index": 9, "category": "alcohol", "confidence": 0.9, "reason": "out of range"},
              {"index": -1, "category": "alcohol", "confidence": 0.9, "reason": "negative"},
              {"index": 0, "category": "not-a-category", "confidence": 0.9, "reason": "unknown"},
              {"index": 0, "category": "banking", "confidence": 0.9, "reason": "never filtered"}
            ]
            """);

        OpenRouterImageHalalClassifier.Parse(body, Batch).Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void Parse_AcceptsImmodestCategory_AndClampsConfidence()
    {
        var body = Wrap("""[{"index": 2, "category": "immodest", "confidence": 5, "reason": "dress"}]""");

        var verdict = OpenRouterImageHalalClassifier.Parse(body, Batch)!.Single().Value;

        verdict.Category.Should().Be("immodest");
        verdict.Confidence.Should().Be(1.0);
    }

    [Fact]
    public void Parse_ContentAsPartsArray_IsConcatenated()
    {
        const string body = """
            {"choices": [{"message": {"content": [
                {"type": "text", "text": "[{\"index\": 0, \"category\": \"pork\","},
                {"type": "text", "text": " \"confidence\": 0.8, \"reason\": \"bacon\"}]"}
            ]}}]}
            """;

        var verdict = OpenRouterImageHalalClassifier.Parse(body, Batch)!.Single().Value;
        verdict.Category.Should().Be("pork");
    }

    [Fact]
    public void Parse_ReturnsNullOnMalformedResponse()
    {
        OpenRouterImageHalalClassifier.Parse("not json", Batch).Should().BeNull();
        OpenRouterImageHalalClassifier.Parse("""{"choices": []}""", Batch).Should().BeNull();
        OpenRouterImageHalalClassifier.Parse(Wrap("no json here"), Batch).Should().BeNull();
    }
}
