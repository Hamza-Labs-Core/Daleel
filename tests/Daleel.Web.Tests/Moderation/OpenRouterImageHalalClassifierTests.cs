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

    [Fact]
    public void VisionPolicy_ComposesPromptFromRuleList()
    {
        var rules = new[]
        {
            new VisionPolicy.Rule("alcohol", "Bottles of wine."),
            new VisionPolicy.Rule("weapons", "Guns or knives marketed as weapons."),
        };

        var prompt = VisionPolicy.Compose(rules);

        prompt.Should().Contain("following rules apply", "the prompt is composed from the rule list");
        prompt.Should().Contain("[alcohol] Bottles of wine.");
        prompt.Should().Contain("[weapons] Guns or knives marketed as weapons.");
        prompt.Should().Contain("JSON array", "the fixed output contract the parser needs is preserved");
    }

    [Fact]
    public void VisionPolicy_Compose_EmptyRules_FallsBackToDefaults()
    {
        VisionPolicy.Compose(Array.Empty<VisionPolicy.Rule>())
            .Should().Be(VisionPolicy.Compose(VisionPolicy.DefaultRules));
    }

    [Fact]
    public void VisionPolicy_AllowedCategories_IncludesRuleCategories_ExcludesNeverFiltered()
    {
        var rules = new[]
        {
            new VisionPolicy.Rule("weapons", "guns"),
            new VisionPolicy.Rule("banking", "riba — must never be a match"),
        };

        var allowed = VisionPolicy.AllowedCategories(rules);

        allowed.Should().Contain("weapons", "an admin rule's category is honoured by the parser");
        allowed.Should().Contain("immodest", "the built-in categories remain available");
        allowed.Should().NotContain("banking", "never-filtered categories are excluded even if a rule names one");
    }

    [Fact]
    public void Parse_HonorsCustomRuleCategory()
    {
        // A category defined only by an admin rule must flag through — not be dropped as "unknown".
        var allowed = VisionPolicy.AllowedCategories(new[] { new VisionPolicy.Rule("weapons", "guns") });
        var body = Wrap("""[{"index": 0, "category": "weapons", "confidence": 0.9, "reason": "rifle"}]""");

        var verdict = OpenRouterImageHalalClassifier.Parse(body, Batch, allowed)!.Single().Value;

        verdict.Category.Should().Be("weapons");
        verdict.IsHaram.Should().BeTrue();
    }

    [Fact]
    public void Parse_DropsCategoryOutsideActiveRules()
    {
        // With a restricted rule set, a category NOT in it is dropped (the model can't invent categories).
        var allowed = VisionPolicy.AllowedCategories(new[] { new VisionPolicy.Rule("alcohol", "bottles") });
        var body = Wrap("""[{"index": 0, "category": "immodest", "confidence": 0.9, "reason": "x"}]""");

        // "immodest" is a built-in category, so it's still allowed (union with the halal set)...
        OpenRouterImageHalalClassifier.Parse(body, Batch, allowed).Should().ContainSingle();
        // ...but a truly unknown one is dropped.
        var junk = Wrap("""[{"index": 0, "category": "totally-made-up", "confidence": 0.9, "reason": "x"}]""");
        OpenRouterImageHalalClassifier.Parse(junk, Batch, allowed).Should().BeEmpty();
    }
}
