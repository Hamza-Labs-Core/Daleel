using Daleel.Core.Models;
using Daleel.Pipeline;
using FluentAssertions;
using Xunit;

namespace Daleel.Pipeline.Tests;

public class OpinionExtractorTests
{
    [Fact]
    public async Task ExtractAsync_ParsesLlmJsonIntoOpinions()
    {
        const string json = """
            [
              {"subject":"Samsung AR24","sentiment":"positive","rating":4.5,
               "pros":["تبريد قوي","هادئ"],"cons":["السعر مرتفع"],"excerpt":"ممتاز جدا","language":"ar"},
              {"subject":"LG Dual","sentiment":"negative","rating":2,
               "pros":[],"cons":["تعطل بسرعة"],"excerpt":"خربان","language":"ar"}
            ]
            """;
        var extractor = new OpinionExtractor(new FakeLlmClient(json));

        var opinions = await extractor.ExtractAsync("AC", new[] { "post 1", "post 2" });

        opinions.Should().HaveCount(2);
        opinions[0].Subject.Should().Be("Samsung AR24");
        opinions[0].Sentiment.Should().Be(Sentiment.Positive);
        opinions[0].Rating.Should().Be(4.5);
        opinions[0].Pros.Should().Contain("تبريد قوي");
        opinions[1].Sentiment.Should().Be(Sentiment.Negative);
    }

    [Fact]
    public async Task ExtractAsync_ToleratesFencedJson()
    {
        const string fenced = "```json\n[{\"subject\":\"X\",\"sentiment\":\"neutral\"}]\n```";
        var extractor = new OpinionExtractor(new FakeLlmClient(fenced));

        var opinions = await extractor.ExtractAsync("X", new[] { "a post" });

        opinions.Should().ContainSingle();
        opinions[0].Sentiment.Should().Be(Sentiment.Neutral);
    }

    [Fact]
    public async Task ExtractAsync_EmptyInput_SkipsLlm()
    {
        var llm = new FakeLlmClient("[]");
        var extractor = new OpinionExtractor(llm);

        var opinions = await extractor.ExtractAsync("X", Array.Empty<string>());

        opinions.Should().BeEmpty();
        llm.Calls.Should().BeEmpty(); // no posts → no LLM call
    }

    [Fact]
    public async Task ExtractAsync_GarbageResponse_ReturnsEmpty()
    {
        var extractor = new OpinionExtractor(new FakeLlmClient("I could not analyze that."));
        var opinions = await extractor.ExtractAsync("X", new[] { "a post" });
        opinions.Should().BeEmpty();
    }

    [Fact]
    public async Task ExtractAsync_PassesPostsIntoPrompt()
    {
        var llm = new FakeLlmClient("[]");
        var extractor = new OpinionExtractor(llm);

        await extractor.ExtractAsync("AC", new[] { "UNIQUE_POST_MARKER" });

        llm.Calls.Should().ContainSingle();
        llm.Calls[0].Messages[0].Content.Should().Contain("UNIQUE_POST_MARKER");
    }
}
