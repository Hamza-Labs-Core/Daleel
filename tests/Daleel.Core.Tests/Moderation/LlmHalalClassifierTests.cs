using Daleel.Core.Llm;
using Daleel.Core.Moderation;
using FluentAssertions;
using Xunit;

namespace Daleel.Core.Tests.Moderation;

public class LlmHalalClassifierTests
{
    private static readonly IReadOnlyList<HalalCandidate> Batch = new[]
    {
        new HalalCandidate(0, "City Bar & Lounge", "SearchResult", "alcohol", "bar"),
        new HalalCandidate(1, "Samsung Fridge", "SearchResult"),
        new HalalCandidate(2, "Arab Bank — loans", "SearchResult")
    };

    [Fact]
    public void ParseVerdicts_ReadsWellFormedResponse()
    {
        const string response = """
            [
              {"id": 0, "haram": true, "category": "alcohol", "confidence": 0.93, "reason": "serves drinks"},
              {"id": 1, "haram": false, "category": null, "confidence": 0.9, "reason": "appliance"}
            ]
            """;

        var verdicts = LlmHalalClassifier.ParseVerdicts(response, Batch);

        verdicts.Should().HaveCount(2);
        verdicts[0].Should().BeEquivalentTo(new HalalVerdict(0, true, "alcohol", 0.93, "serves drinks"));
        verdicts[1].IsHaram.Should().BeFalse();
    }

    [Fact]
    public void ParseVerdicts_StripsMarkdownFences()
    {
        const string response = """
            ```json
            [{"id": 0, "haram": true, "category": "alcohol", "confidence": 0.8, "reason": "bar"}]
            ```
            """;

        LlmHalalClassifier.ParseVerdicts(response, Batch).Should().ContainSingle();
    }

    [Theory]
    [InlineData("riba")]
    [InlineData("banking")]
    [InlineData("finance")]
    [InlineData("interest")]
    public void ParseVerdicts_DiscardsNeverFilteredCategories(string category)
    {
        // Hard policy backstop: even if the model flags a bank as haram, the verdict is dropped.
        var response = $$"""[{"id": 2, "haram": true, "category": "{{category}}", "confidence": 0.99, "reason": "riba"}]""";

        LlmHalalClassifier.ParseVerdicts(response, Batch).Should().BeEmpty();
    }

    [Fact]
    public void ParseVerdicts_DiscardsUnknownCategoriesAndIds()
    {
        const string response = """
            [
              {"id": 0, "haram": true, "category": "made-up-category", "confidence": 0.9, "reason": "?"},
              {"id": 99, "haram": true, "category": "alcohol", "confidence": 0.9, "reason": "unknown id"}
            ]
            """;

        LlmHalalClassifier.ParseVerdicts(response, Batch).Should().BeEmpty();
    }

    [Fact]
    public void ParseVerdicts_ClampsConfidence()
    {
        const string response = """[{"id": 0, "haram": true, "category": "alcohol", "confidence": 7.5, "reason": "x"}]""";

        LlmHalalClassifier.ParseVerdicts(response, Batch).Single().Confidence.Should().Be(1.0);
    }

    [Fact]
    public void ParseVerdicts_ReturnsEmptyOnGarbage()
    {
        LlmHalalClassifier.ParseVerdicts("The items look fine to me!", Batch).Should().BeEmpty();
        LlmHalalClassifier.ParseVerdicts(null, Batch).Should().BeEmpty();
    }

    [Fact]
    public void ParseVerdicts_DedupesRepeatedIds_HaramWins()
    {
        // Models sometimes emit the same item twice (hinted + haram both match the prompt's
        // response rules). At most one verdict per id may survive — and never a halal one when a
        // duplicate said haram, so a duplicate can't accidentally overturn a real flag.
        const string response = """
            [
              {"id": 0, "haram": false, "category": null, "confidence": 0.6, "reason": "hinted item"},
              {"id": 0, "haram": true, "category": "alcohol", "confidence": 0.9, "reason": "sells drinks"},
              {"id": 0, "haram": false, "category": null, "confidence": 0.7, "reason": "again"}
            ]
            """;

        var verdicts = LlmHalalClassifier.ParseVerdicts(response, Batch);

        var verdict = verdicts.Should().ContainSingle().Subject;
        verdict.IsHaram.Should().BeTrue();
        verdict.Category.Should().Be("alcohol");
    }

    [Fact]
    public async Task ClassifyAsync_SendsKeywordHints_AndParsesRoundTrip()
    {
        var llm = new FakeLlm("""[{"id": 0, "haram": false, "category": null, "confidence": 0.9, "reason": "barber shop"}]""");
        var classifier = new LlmHalalClassifier(llm);

        var result = await classifier.ClassifyAsync(new[]
        {
            new HalalCandidate(0, "Barber shop downtown", "StoreLocation", "alcohol", "bar")
        });

        result.Verdicts.Should().ContainSingle().Which.IsHaram.Should().BeFalse();
        result.UnansweredIds.Should().BeEmpty();
        llm.LastUserPrompt.Should().Contain("keyword hint: alcohol").And.Contain("\"bar\"");
    }

    [Fact]
    public async Task ClassifyAsync_TransportError_ReportsCandidatesUnanswered()
    {
        var classifier = new LlmHalalClassifier(new ThrowingLlm());

        var result = await classifier.ClassifyAsync(new[] { new HalalCandidate(7, "anything", "Item") });

        result.Verdicts.Should().BeEmpty();
        result.UnansweredIds.Should().ContainSingle().Which.Should().Be(7);
    }

    [Fact]
    public async Task ClassifyAsync_GarbageResponseWithHints_ReportsBatchUnanswered()
    {
        // Hinted candidates demand explicit verdicts — a batch that parses to nothing didn't answer.
        var classifier = new LlmHalalClassifier(new FakeLlm("The items look fine to me!"));

        var result = await classifier.ClassifyAsync(new[]
        {
            new HalalCandidate(0, "City Bar & Lounge", "SearchResult", "alcohol", "bar"),
            new HalalCandidate(1, "Samsung Fridge", "SearchResult")
        });

        result.Verdicts.Should().BeEmpty();
        result.UnansweredIds.Should().BeEquivalentTo(new[] { 0, 1 });
    }

    [Fact]
    public async Task ClassifyAsync_PartialBatchFailure_ReportsOnlyTheFailedBatch()
    {
        // 81 candidates → two batches. First call answers, second throws: only the second
        // batch's ids are reported unanswered, the first batch's verdicts survive.
        var llm = new SequenceLlm(
            """[{"id": 0, "haram": true, "category": "alcohol", "confidence": 0.9, "reason": "bar"}]""");
        var classifier = new LlmHalalClassifier(llm);
        var candidates = Enumerable.Range(0, 81)
            .Select(i => new HalalCandidate(i, $"item {i}", "SearchResult",
                i == 0 ? "alcohol" : null, i == 0 ? "bar" : null))
            .ToList();

        var result = await classifier.ClassifyAsync(candidates);

        result.Verdicts.Should().ContainSingle().Which.Id.Should().Be(0);
        result.UnansweredIds.Should().ContainSingle().Which.Should().Be(80);
    }

    private sealed class FakeLlm : ILlmClient
    {
        private readonly string _response;
        public string? LastUserPrompt { get; private set; }
        public string Provider => "fake";

        public FakeLlm(string response) => _response = response;

        public Task<LlmResponse> CompleteAsync(
            string systemPrompt, IReadOnlyList<LlmMessage> messages, CancellationToken cancellationToken = default)
        {
            LastUserPrompt = messages[^1].Content;
            return Task.FromResult(new LlmResponse { Content = _response });
        }
    }

    /// <summary>Answers the first call with the given response, throws on every later call.</summary>
    private sealed class SequenceLlm : ILlmClient
    {
        private readonly string _firstResponse;
        private int _calls;
        public string Provider => "fake";

        public SequenceLlm(string firstResponse) => _firstResponse = firstResponse;

        public Task<LlmResponse> CompleteAsync(
            string systemPrompt, IReadOnlyList<LlmMessage> messages, CancellationToken cancellationToken = default) =>
            Interlocked.Increment(ref _calls) == 1
                ? Task.FromResult(new LlmResponse { Content = _firstResponse })
                : throw new HttpRequestException("second batch down");
    }

    private sealed class ThrowingLlm : ILlmClient
    {
        public string Provider => "fake";
        public Task<LlmResponse> CompleteAsync(
            string systemPrompt, IReadOnlyList<LlmMessage> messages, CancellationToken cancellationToken = default) =>
            throw new HttpRequestException("provider down");
    }
}

public class HalalPolicyTests
{
    [Fact]
    public void DefaultThreshold_IsShowByDefaultHigh()
    {
        new HalalPolicy().DefaultThreshold.Should().Be(0.8);
    }

    [Fact]
    public void ThresholdFromPrecision_KeepsFallback_UnderMinSample()
    {
        HalalPolicy.ThresholdFromPrecision(correct: 2, incorrect: 1).Should().Be(0.8);
    }

    [Fact]
    public void ThresholdFromPrecision_PerfectPrecision_NeverDropsBelowTheFloor()
    {
        // Even a perfectly-rated category keeps the show-by-default bias.
        HalalPolicy.ThresholdFromPrecision(correct: 10, incorrect: 0).Should().Be(0.65);
    }

    [Fact]
    public void ThresholdFromPrecision_ZeroPrecision_DemandsNearCertainty()
    {
        HalalPolicy.ThresholdFromPrecision(correct: 0, incorrect: 10).Should().Be(0.95);
    }

    [Fact]
    public void ThresholdFromPrecision_MidPrecision_LandsBetween()
    {
        var t = HalalPolicy.ThresholdFromPrecision(correct: 6, incorrect: 4); // 60% precision
        t.Should().BeApproximately(0.77, 0.001);
    }
}

public class ModerationKeysTests
{
    [Fact]
    public void HashContent_IsCaseAndWhitespaceInvariant()
    {
        var a = ModerationKeys.HashContent("Imported  Beer   6-pack");
        var b = ModerationKeys.HashContent("  imported beer 6-pack ");
        a.Should().Be(b);
    }

    [Fact]
    public void HashContent_DiffersForDifferentContent()
    {
        ModerationKeys.HashContent("beer").Should().NotBe(ModerationKeys.HashContent("fridge"));
    }
}
