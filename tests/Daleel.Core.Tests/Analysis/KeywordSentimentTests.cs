using Daleel.Core.Analysis;
using Daleel.Core.Models;
using FluentAssertions;
using Xunit;

namespace Daleel.Core.Tests.Analysis;

public class KeywordSentimentTests
{
    [Theory]
    [InlineData("This product is excellent and I love it", Sentiment.Positive)]
    [InlineData("ممتاز ورائع جدا", Sentiment.Positive)]
    [InlineData("worst purchase, totally broken", Sentiment.Negative)]
    [InlineData("سيء ومشكلة كبيرة", Sentiment.Negative)]
    [InlineData("It is a phone", Sentiment.Neutral)]
    [InlineData("", Sentiment.Neutral)]
    [InlineData(null, Sentiment.Neutral)]
    public void Score_ClassifiesBilingualCues(string? text, Sentiment expected)
    {
        KeywordSentiment.Score(text).Should().Be(expected);
    }

    [Fact]
    public void Score_TiesResolveToNeutral()
    {
        // One positive ("good") and one negative ("bad") cue → neither wins.
        KeywordSentiment.Score("good but also bad").Should().Be(Sentiment.Neutral);
    }
}
