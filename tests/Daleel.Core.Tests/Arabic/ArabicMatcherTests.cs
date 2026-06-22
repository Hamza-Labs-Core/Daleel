using Daleel.Core.Arabic;
using Daleel.Core.Models;
using FluentAssertions;
using Xunit;

namespace Daleel.Core.Tests.Arabic;

public class ArabicMatcherTests
{
    private readonly ArabicMatcher _matcher = new();

    // ── Cases called out explicitly in the project spec ──────────────────────

    [Fact]
    public void Match_KeywordWithDiacritics_MatchesStrippedForm()
    {
        var result = _matcher.Match(
            "هذا نص يتحدث عن شركة الاتصالات",
            new[] { "شَرِكَة" },
            MatchMode.Contains);

        result.IsMatch.Should().BeTrue();
        result.Score.Should().Be(1.0);
        result.MatchedKeyword.Should().Be("شَرِكَة");
    }

    [Fact]
    public void Match_TaaMarbuta_MatchesHaaForm()
    {
        // keyword شركة (taa marbuta) vs text شركه (haa)
        var result = _matcher.Match("نشرت الشركه بيانا", new[] { "شركة" }, MatchMode.Contains);
        result.IsMatch.Should().BeTrue();
    }

    [Fact]
    public void Match_HamzaAlef_MatchesBareAlef()
    {
        // keyword أخبار (hamza alef) vs text اخبار (bare alef)
        var result = _matcher.Match("اخبار عاجلة الان", new[] { "أخبار" }, MatchMode.Contains);
        result.IsMatch.Should().BeTrue();
    }

    [Fact]
    public void Match_UnrelatedText_DoesNotMatch()
    {
        var result = _matcher.Match("القطة تجلس على الطاوله", new[] { "شركة" }, MatchMode.Contains);
        result.IsMatch.Should().BeFalse();
        result.Score.Should().Be(0.0);
    }

    [Fact]
    public void Match_MultiKeyword_AnyMatchIsHit()
    {
        var keywords = new[] { "بنك", "شركة", "وزارة" };
        var result = _matcher.Match("أعلنت الشركه عن نتائجها", keywords, MatchMode.Contains);

        result.IsMatch.Should().BeTrue();
        result.MatchedKeyword.Should().Be("شركة");
    }

    [Fact]
    public void Match_MultiKeyword_NoneMatch_IsMiss()
    {
        var keywords = new[] { "بنك", "وزارة" };
        var result = _matcher.Match("القطة على الطاوله", keywords, MatchMode.Contains);
        result.IsMatch.Should().BeFalse();
    }

    // ── Mode coverage ────────────────────────────────────────────────────────

    [Fact]
    public void Match_Exact_RequiresWholeStringEquality()
    {
        _matcher.Match("شركة", new[] { "شَرِكَة" }, MatchMode.Exact).IsMatch.Should().BeTrue();
        _matcher.Match("شركة الاتصالات", new[] { "شركة" }, MatchMode.Exact).IsMatch.Should().BeFalse();
    }

    [Fact]
    public void Match_Contains_FindsSubstring()
    {
        var result = _matcher.Match("الشركة الوطنية للاتصالات", new[] { "الوطنية" }, MatchMode.Contains);
        result.IsMatch.Should().BeTrue();
        result.Context.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Match_Fuzzy_ToleratesSingleCharTypo()
    {
        // keyword "الاتصالات" vs token "الاتصلات" (missing one alef) — within threshold.
        var result = _matcher.Match("شركة الاتصلات الوطنية", new[] { "الاتصالات" }, MatchMode.Fuzzy, 0.2);
        result.IsMatch.Should().BeTrue();
        result.Score.Should().BeLessThan(1.0).And.BeGreaterThan(0.5);
    }

    [Fact]
    public void Match_Fuzzy_RejectsTooManyEdits()
    {
        var result = _matcher.Match("القطة على الطاوله", new[] { "الاتصالات" }, MatchMode.Fuzzy, 0.2);
        result.IsMatch.Should().BeFalse();
    }

    [Fact]
    public void Match_Fuzzy_ExactSubstringScoresOne()
    {
        var result = _matcher.Match("شركة الاتصالات الوطنية", new[] { "الاتصالات" }, MatchMode.Fuzzy, 0.2);
        result.IsMatch.Should().BeTrue();
        result.Score.Should().Be(1.0);
    }

    // ── Guard rails ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Match_EmptyText_IsMiss(string? text)
    {
        _matcher.Match(text!, new[] { "شركة" }).IsMatch.Should().BeFalse();
    }

    [Fact]
    public void Match_EmptyKeywordList_IsMiss()
    {
        _matcher.Match("شركة الاتصالات", System.Array.Empty<string>()).IsMatch.Should().BeFalse();
    }

    [Fact]
    public void Match_KeywordWithTatweel_MatchesPlainText()
    {
        var result = _matcher.Match("خبر عاجل", new[] { "خـــبر" }, MatchMode.Contains);
        result.IsMatch.Should().BeTrue();
    }

    // ── Levenshtein primitive ────────────────────────────────────────────────

    [Theory]
    [InlineData("kitten", "sitting", 3)]
    [InlineData("flaw", "lawn", 2)]
    [InlineData("same", "same", 0)]
    [InlineData("", "abc", 3)]
    [InlineData("abc", "", 3)]
    public void Levenshtein_ComputesEditDistance(string a, string b, int expected)
    {
        ArabicMatcher.Levenshtein(a, b).Should().Be(expected);
    }
}
