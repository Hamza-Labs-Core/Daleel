using Daleel.Web.Pipeline;
using FluentAssertions;
using Xunit;

namespace Daleel.Web.Tests.Pipeline;

/// <summary>
/// QueryScope must strip market scope + filler from an ARABIC query too — otherwise "سرير هزاز للرضع
/// في الاردن" is typed whole into a store's search box, AND-matches "في الاردن" against product titles,
/// and a stocked store answers with its no-results page (the bug that returned bare, unenriched cards).
/// </summary>
public class QueryScopeArabicTests
{
    [Fact]
    public void StripsArabicGeoScopeAndPreposition()
    {
        // rocking crib for infants IN JORDAN → only the product tokens remain.
        QueryScope.StoreSearchTerm("سرير هزاز للرضع في الاردن", "jordan")
            .Should().Be("سرير هزاز للرضع");
    }

    [Fact]
    public void StripsArabicIntentFillerAndCentreCity()
    {
        // "افضل سعر مكيف في عمان" (best price AC in Amman) → "مكيف".
        QueryScope.SignificantTokens("افضل سعر مكيف في عمان", "jordan")
            .Should().Equal("مكيف");
    }

    [Fact]
    public void FoldsAlefHamzaAndStripsDefiniteArticle_ForGeoMatch()
    {
        // "الأردن" (with hamza) folds to the same key as the alias "الاردن" and the article is stripped.
        QueryScope.SignificantTokens("عربة اطفال الأردن", "jordan")
            .Should().Equal("عربة", "اطفال");
    }

    [Fact]
    public void KeepsArabicProductWordsThatMerelyStartWithArticle()
    {
        // Normalization is for MATCHING only; a product word beginning with "ال" that isn't geo/filler
        // is kept in its original form.
        QueryScope.StoreSearchTerm("الكترونيات الاطفال في الاردن", "jordan")
            .Should().Be("الكترونيات الاطفال");
    }

    [Fact]
    public void EnglishBehaviourIsUnchanged()
    {
        QueryScope.StoreSearchTerm("diapers Jordan", "jordan").Should().Be("diapers");
        QueryScope.StoreSearchTerm("best coffee grinder in Amman", "jordan").Should().Be("coffee grinder");
    }

    [Theory]
    [InlineData("الأردن", "اردن")]   // hamza folded, article stripped
    [InlineData("الاردن", "اردن")]
    [InlineData("عمّان", "عمان")]     // shadda dropped
    [InlineData("مكيف", "مكيف")]      // product word untouched
    [InlineData("Jordan", "jordan")] // latin lower-cased, article rule doesn't fire
    public void NormalizeForMatch_FoldsSpellingVariants(string input, string expected)
    {
        QueryScope.NormalizeForMatch(input).Should().Be(expected);
    }
}
