using Daleel.Core.Models;
using FluentAssertions;
using Xunit;

namespace Daleel.Agent.Tests;

/// <summary>
/// The deterministic backstop for planner misclassification: an LLM that labels "best espresso
/// machine" as General silently skips the whole product pipeline (QA job 4, 2026-07-02). These pin
/// the coercion semantics: only General is ever upgraded, only for unmistakably buy-intent phrasing,
/// and advice-shaped "best …" questions are left alone.
/// </summary>
public class BuyIntentHeuristicTests
{
    [Theory]
    [InlineData("best espresso machine")]
    [InlineData("Best Air Fryer")]
    [InlineData("top gaming laptops")]
    [InlineData("the best 55 inch tv")]
    [InlineData("buy air fryer")]
    [InlineData("cheapest iphone 15")]
    [InlineData("washing machine price in amman")]
    [InlineData("air fryer deals")]
    [InlineData("أفضل قلاية هوائية")]
    [InlineData("سعر ايفون 15")]
    [InlineData("مكيف سبليت للبيع")]
    public void LooksLikeBuyIntent_Matches_ShoppingPhrasing(string query) =>
        BuyIntentHeuristic.LooksLikeBuyIntent(query).Should().BeTrue(query);

    [Theory]
    [InlineData("best way to learn arabic")]
    [InlineData("best time to visit petra")]
    [InlineData("how to descale a coffee maker")]
    [InlineData("what is the capital of jordan")]
    [InlineData("Samsung")]
    [InlineData("best method for memorizing quran")]
    [InlineData("كيف أتعلم البرمجة")]
    [InlineData("")]
    [InlineData(null)]
    public void LooksLikeBuyIntent_Ignores_AdviceAndNonShopping(string? query) =>
        BuyIntentHeuristic.LooksLikeBuyIntent(query).Should().BeFalse(query ?? "(null)");

    [Fact]
    public void Coerce_UpgradesOnlyGeneral()
    {
        // The canonical fix: General + buy phrasing → ProductResearch.
        BuyIntentHeuristic.Coerce(QueryType.General, "best espresso machine")
            .Should().Be(QueryType.ProductResearch);

        // A specific LLM classification is never overridden…
        BuyIntentHeuristic.Coerce(QueryType.BrandLookup, "best espresso machine")
            .Should().Be(QueryType.BrandLookup);
        BuyIntentHeuristic.Coerce(QueryType.OpinionAggregation, "cheapest iphone 15")
            .Should().Be(QueryType.OpinionAggregation);

        // …and General stays General for non-shopping queries.
        BuyIntentHeuristic.Coerce(QueryType.General, "best way to learn arabic")
            .Should().Be(QueryType.General);
    }
}
