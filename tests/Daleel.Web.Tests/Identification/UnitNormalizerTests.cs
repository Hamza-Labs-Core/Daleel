using Daleel.Web.Identification;
using FluentAssertions;
using Xunit;

namespace Daleel.Web.Tests.Identification;

/// <summary>
/// Pins the unit canonicalization that lets the same measurement quoted three different ways collapse to
/// one value — the headline case being screen/display size across <c>55 inches</c> / <c>55"</c> /
/// <c>139.7cm</c>.
/// </summary>
public class UnitNormalizerTests
{
    [Theory]
    [InlineData("55 inches")]
    [InlineData("55\"")]
    [InlineData("55 inch")]
    [InlineData("55in")]
    [InlineData("139.7cm")]
    [InlineData("139.7 cm")]
    [InlineData("1397 mm")]
    public void Normalize_FoldsLengthUnitsToCanonicalDualForm(string input)
    {
        UnitNormalizer.Normalize(input).Should().Be("55 inches / 139.7 cm");
    }

    [Fact]
    public void Normalize_KeepsOneDecimalAndDropsTrailingZero()
    {
        // 60 inches → 152.4 cm (one decimal kept); a whole cm value drops its ".0".
        UnitNormalizer.Normalize("60\"").Should().Be("60 inches / 152.4 cm");
    }

    [Theory]
    [InlineData("  55   inch ")]
    [InlineData("55\tinch")]
    public void Normalize_CollapsesWhitespaceBeforeParsing(string input)
    {
        UnitNormalizer.Normalize(input).Should().Be("55 inches / 139.7 cm");
    }

    [Theory]
    [InlineData("A++", "A++")]
    [InlineData("12000 BTU", "12000 BTU")]
    [InlineData("  Wi-Fi 6 ", "Wi-Fi 6")]
    public void Normalize_LeavesNonLengthValuesTrimmedButOtherwiseUnchanged(string input, string expected)
    {
        UnitNormalizer.Normalize(input).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Normalize_ReturnsEmptyForBlankInput(string? input)
    {
        UnitNormalizer.Normalize(input).Should().BeEmpty();
    }
}
