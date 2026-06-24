using Daleel.Core.Models;
using FluentAssertions;
using Xunit;

namespace Daleel.Core.Tests.Models;

public class SocialProofTests
{
    [Fact]
    public void SentimentBreakdown_CountsByPolarity()
    {
        var social = new SocialProof
        {
            Reviews = new[]
            {
                new UserReview { Quote = "great", Sentiment = Sentiment.Positive },
                new UserReview { Quote = "good", Sentiment = Sentiment.Positive },
                new UserReview { Quote = "broke", Sentiment = Sentiment.Negative },
                new UserReview { Quote = "ok", Sentiment = Sentiment.Neutral },
            }
        };

        social.Positive.Should().Be(2);
        social.Negative.Should().Be(1);
        social.Neutral.Should().Be(1);
        social.HasReviews.Should().BeTrue();
        social.Summary.NetScore.Should().BeApproximately((2 - 1) / 4.0, 0.001);
    }

    [Fact]
    public void Empty_HasNoReviews()
    {
        new SocialProof().HasReviews.Should().BeFalse();
    }
}
