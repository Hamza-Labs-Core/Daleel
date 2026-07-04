using Daleel.Core.Moderation;
using FluentAssertions;
using Xunit;

namespace Daleel.Core.Tests.Moderation;

/// <summary>Dynamic rule compilation: suppressions and additions over the static defaults.</summary>
public class DynamicRulesTests
{
    [Fact]
    public void NoRules_ReturnsTheSharedDefaults()
    {
        ContentFilter.BuildCategories(Array.Empty<ModerationRule>())
            .Should().BeSameAs(ContentFilter.BuildCategories(Array.Empty<ModerationRule>()),
                "no-rule compilation must not allocate fresh regexes per call");
    }

    [Fact]
    public void SuppressedArabicTerm_StopsMatching_OthersUnaffected()
    {
        var categories = ContentFilter.BuildCategories(new[]
        {
            new ModerationRule(ModerationRule.SuppressTerm, "alcohol", "بار", "ar")
        });
        var filter = new ContentFilter(FilterStrictness.Strict, categories: categories);

        filter.IsHalal("سهرة في بار الفندق").Should().BeTrue("the term is suppressed");
        filter.IsHalal("رحنا إلى البار").Should().BeTrue();
        filter.IsHalal("بيرة مستوردة").Should().BeFalse("other terms in the category still apply");
        filter.IsHalal("City Bar & Lounge").Should().BeFalse("the ENGLISH bar term is untouched");
    }

    [Fact]
    public void SuppressionTermWithArticlePrefix_SuppressesTheListTerm()
    {
        // The finding's Rule holds the matched value, which may carry the article ("البار") —
        // it must still suppress the underlying list term.
        var categories = ContentFilter.BuildCategories(new[]
        {
            new ModerationRule(ModerationRule.SuppressTerm, "alcohol", "البار", "ar")
        });
        var filter = new ContentFilter(FilterStrictness.Strict, categories: categories);

        filter.IsHalal("بار ومطعم في وسط البلد").Should().BeTrue();
    }

    [Fact]
    public void SuppressedEnglishTerm_StopsMatching()
    {
        var categories = ContentFilter.BuildCategories(new[]
        {
            new ModerationRule(ModerationRule.SuppressTerm, "alcohol", "bar", "en")
        });
        var filter = new ContentFilter(FilterStrictness.Strict, categories: categories);

        filter.IsHalal("City Bar & Lounge").Should().BeTrue();
        filter.IsHalal("Best wine deals this week").Should().BeFalse();
    }

    [Fact]
    public void AddedTerm_StartsMatching()
    {
        var categories = ContentFilter.BuildCategories(new[]
        {
            new ModerationRule(ModerationRule.AddTerm, "alcohol", "arak", "en"),
            new ModerationRule(ModerationRule.AddTerm, "alcohol", "عرق بلدي", "ar")
        });
        var filter = new ContentFilter(FilterStrictness.Strict, categories: categories);

        filter.IsHalal("Traditional arak bottle 750ml").Should().BeFalse();
        filter.IsHalal("عرق بلدي للبيع").Should().BeFalse();
        new ContentFilter(FilterStrictness.Strict).IsHalal("Traditional arak bottle 750ml")
            .Should().BeTrue("the default set must be unaffected");
    }

    [Fact]
    public void SuppressingEveryTermInOneLanguage_MatchesNothing_NotEverything()
    {
        // An empty alternation regex matches EVERYWHERE — the never-matches guard must kick in.
        var rules = new[]
        {
            "porn", "pornography", "xxx", "nude", "nudity", "escort", "adult content", "erotic", "sex shop"
        }.Select(t => new ModerationRule(ModerationRule.SuppressTerm, "adult", t, "en")).ToArray();
        var filter = new ContentFilter(FilterStrictness.Strict,
            categories: ContentFilter.BuildCategories(rules));

        filter.IsHalal("Samsung air conditioner").Should().BeTrue();
        filter.IsHalal("إباحية").Should().BeFalse("the Arabic side of the category is intact");
    }

    [Fact]
    public void UnknownCategoryRules_AreIgnored()
    {
        var categories = ContentFilter.BuildCategories(new[]
        {
            new ModerationRule(ModerationRule.AddTerm, "riba", "loan", "en") // no such category
        });

        new ContentFilter(FilterStrictness.Strict, categories: categories)
            .IsHalal("Personal loan offers").Should().BeTrue();
    }
}
