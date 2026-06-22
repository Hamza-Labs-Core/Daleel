using Daleel.Core.Arabic;
using FluentAssertions;
using Xunit;

namespace Daleel.Core.Tests.Arabic;

public class ArabicNormalizerTests
{
    [Theory]
    [InlineData("أحمد", "احمد")]   // alef with hamza above
    [InlineData("إسلام", "اسلام")] // alef with hamza below
    [InlineData("آمن", "امن")]     // alef with madda
    [InlineData("ٱلله", "الله")]   // alef wasla
    public void Normalize_FoldsAlefVariants(string input, string expected)
    {
        ArabicNormalizer.Normalize(input).Should().Be(expected);
    }

    [Fact]
    public void Normalize_StripsDiacritics()
    {
        ArabicNormalizer.Normalize("شَرِكَة").Should().Be("شركه");
    }

    [Fact]
    public void Normalize_FoldsAlefMaksuraToYaa()
    {
        // مصطفى → مصطفي
        ArabicNormalizer.Normalize("مصطفى").Should().Be("مصطفي");
    }

    [Fact]
    public void Normalize_FoldsTaaMarbutaToHaa()
    {
        ArabicNormalizer.Normalize("مدرسة").Should().Be("مدرسه");
    }

    [Fact]
    public void Normalize_RemovesTatweel()
    {
        // خـــبر → خبر
        ArabicNormalizer.Normalize("خـــبر").Should().Be("خبر");
    }

    [Theory]
    [InlineData("مؤمن", "مومن")]  // hamza on waw → waw
    [InlineData("سائل", "سايل")]  // hamza on yaa → yaa
    public void Normalize_FoldsHamzaCarriers(string input, string expected)
    {
        ArabicNormalizer.Normalize(input).Should().Be(expected);
    }

    [Fact]
    public void Normalize_DropsStandaloneHamza()
    {
        // شيء → شي
        ArabicNormalizer.Normalize("شيء").Should().Be("شي");
    }

    [Fact]
    public void Normalize_CollapsesWhitespace()
    {
        ArabicNormalizer.Normalize("شركة    الاتصالات\t\nالوطنية")
            .Should().Be("شركه الاتصالات الوطنيه");
    }

    [Fact]
    public void Normalize_TrimsEnds()
    {
        ArabicNormalizer.Normalize("   شركة   ").Should().Be("شركه");
    }

    [Fact]
    public void Normalize_ConvertsArabicIndicDigits()
    {
        ArabicNormalizer.Normalize("٢٠٢٤").Should().Be("2024");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Normalize_HandlesNullAndEmpty(string? input)
    {
        ArabicNormalizer.Normalize(input).Should().BeEmpty();
    }

    [Fact]
    public void Normalize_IsIdempotent()
    {
        var once = ArabicNormalizer.Normalize("أخبار الشَّرِكَة الوطنيّة");
        var twice = ArabicNormalizer.Normalize(once);
        twice.Should().Be(once);
    }

    [Fact]
    public void Normalize_DifferentVariantsConvergeToSameForm()
    {
        // Two spellings of the same word should normalize identically.
        var a = ArabicNormalizer.Normalize("شَرِكَة");
        var b = ArabicNormalizer.Normalize("شركه");
        a.Should().Be(b);
    }
}
