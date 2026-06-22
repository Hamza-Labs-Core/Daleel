using Daleel.Core.Arabic;
using FluentAssertions;
using Xunit;

namespace Daleel.Core.Tests.Arabic;

public class DiacriticStripperTests
{
    [Fact]
    public void Strip_RemovesFathaKasraDamma()
    {
        // شَرِكَة (sha-fatha, ra-kasra, ka-fatha, ta-marbuta) → شركة
        DiacriticStripper.Strip("شَرِكَة").Should().Be("شركة");
    }

    [Fact]
    public void Strip_RemovesShaddaAndSukun()
    {
        DiacriticStripper.Strip("مُحَمَّدْ").Should().Be("محمد");
    }

    [Fact]
    public void Strip_RemovesTanwin()
    {
        // كتابٌ with damatan → كتاب
        DiacriticStripper.Strip("كتابٌ").Should().Be("كتاب");
    }

    [Fact]
    public void Strip_RemovesSuperscriptAlef()
    {
        // هٰذا (with dagger alef U+0670) → هذا
        DiacriticStripper.Strip("هٰذا").Should().Be("هذا");
    }

    [Fact]
    public void Strip_LeavesBaseLettersUntouched()
    {
        DiacriticStripper.Strip("شركة").Should().Be("شركة");
    }

    [Fact]
    public void Strip_LeavesNonArabicTextUntouched()
    {
        DiacriticStripper.Strip("Hello, World!").Should().Be("Hello, World!");
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    public void Strip_HandlesNullAndEmpty(string? input, string expected)
    {
        DiacriticStripper.Strip(input).Should().Be(expected);
    }

    [Theory]
    [InlineData('ً', true)]  // fathatan
    [InlineData('ِ', true)]  // kasra
    [InlineData('ْ', true)]  // sukun
    [InlineData('ٰ', true)]  // superscript alef
    [InlineData('ا', false)] // alef (base letter)
    [InlineData('A', false)]      // latin
    public void IsArabicDiacritic_ClassifiesCorrectly(char ch, bool expected)
    {
        DiacriticStripper.IsArabicDiacritic(ch).Should().Be(expected);
    }
}
