using Daleel.Web.Data;
using FluentAssertions;
using Xunit;

namespace Daleel.Web.Tests;

public class RelevanceFlagTests
{
    [Theory]
    [InlineData("  Women   Pants ", "women pants")]
    [InlineData("LAPTOP", "laptop")]
    [InlineData("best  AC\tin Jordan", "best ac in jordan")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void QueryKeyOf_NormalizesWhitespaceAndCase(string? input, string expected)
    {
        RelevanceFlag.QueryKeyOf(input).Should().Be(expected);
    }
}
