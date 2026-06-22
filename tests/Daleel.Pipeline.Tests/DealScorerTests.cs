using Daleel.Core.Models;
using Daleel.Pipeline;
using FluentAssertions;
using Xunit;

namespace Daleel.Pipeline.Tests;

public class DealScorerTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 22, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Score_ExpiredDeal_IsZero()
    {
        var scorer = new DealScorer(Now);
        var deal = new DealListing
        {
            Title = "old",
            DiscountPercent = 50,
            Expiry = Now.AddDays(-1)
        };

        scorer.Score(deal).Should().Be(0.0);
    }

    [Fact]
    public void Score_DeeperDiscount_ScoresHigher()
    {
        var scorer = new DealScorer(Now);
        var shallow = new DealListing { Title = "a", DiscountPercent = 10, FoundAt = Now };
        var deep = new DealListing { Title = "b", DiscountPercent = 60, FoundAt = Now };

        scorer.Score(deep).Should().BeGreaterThan(scorer.Score(shallow));
    }

    [Fact]
    public void Score_DerivesDiscountFromPrices()
    {
        var scorer = new DealScorer(Now);
        var deal = new DealListing
        {
            Title = "c",
            Price = new Money(50, "JOD"),
            OriginalPrice = new Money(100, "JOD"),
            FoundAt = Now
        };

        // 50% discount derived; with weights 0.5*0.5 + 0.3*1.0(recency) + 0.2*0.5(default reliability)
        scorer.Score(deal).Should().BeApproximately(0.25 + 0.30 + 0.10, 0.001);
    }

    [Fact]
    public void Score_NewerDeal_ScoresHigherThanOlder()
    {
        var scorer = new DealScorer(Now, TimeSpan.FromDays(10));
        var fresh = new DealListing { Title = "x", DiscountPercent = 30, FoundAt = Now };
        var old = new DealListing { Title = "y", DiscountPercent = 30, FoundAt = Now.AddDays(-9) };

        scorer.Score(fresh).Should().BeGreaterThan(scorer.Score(old));
    }

    [Fact]
    public void Score_HigherReliability_ScoresHigher()
    {
        var scorer = new DealScorer(Now);
        var trusted = new DealListing { Title = "t", DiscountPercent = 20, FoundAt = Now, StoreReliability = 1.0 };
        var sketchy = new DealListing { Title = "s", DiscountPercent = 20, FoundAt = Now, StoreReliability = 0.0 };

        scorer.Score(trusted).Should().BeGreaterThan(scorer.Score(sketchy));
    }

    [Fact]
    public void Rank_SortsBestFirstAndStampsScore()
    {
        var scorer = new DealScorer(Now);
        var deals = new[]
        {
            new DealListing { Title = "weak", DiscountPercent = 5, FoundAt = Now },
            new DealListing { Title = "strong", DiscountPercent = 70, FoundAt = Now },
            new DealListing { Title = "expired", DiscountPercent = 90, Expiry = Now.AddDays(-2), FoundAt = Now },
        };

        var ranked = scorer.Rank(deals);

        ranked[0].Title.Should().Be("strong");
        ranked[^1].Title.Should().Be("expired");
        ranked[0].Score.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Score_StaysInUnitRange()
    {
        var scorer = new DealScorer(Now);
        var extreme = new DealListing
        {
            Title = "max",
            DiscountPercent = 100,
            FoundAt = Now,
            StoreReliability = 1.0
        };

        scorer.Score(extreme).Should().BeInRange(0.0, 1.0);
    }
}
