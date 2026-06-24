using Daleel.Core.Models;
using FluentAssertions;
using Xunit;

namespace Daleel.Core.Tests.Models;

public class CurrencyConverterTests
{
    [Fact]
    public void Convert_BetweenKnownCurrencies_ReturnsApproxValue()
    {
        // 510 AED at ~0.193 JOD/AED ≈ 98.4 JOD.
        var jod = CurrencyConverter.Convert(510, "AED", "JOD");
        jod.Should().NotBeNull();
        jod!.Value.Should().BeApproximately(98.43m, 0.5m);
    }

    [Fact]
    public void Convert_SameCurrency_IsNotConverted()
    {
        CurrencyConverter.CanConvert("JOD", "JOD").Should().BeFalse();
        CurrencyConverter.Convert(100, "JOD", "JOD").Should().BeNull();
    }

    [Fact]
    public void Convert_UnknownCurrency_ReturnsNull()
    {
        CurrencyConverter.Convert(100, "XYZ", "JOD").Should().BeNull();
        CurrencyConverter.CanConvert("JOD", "XYZ").Should().BeFalse();
    }

    [Fact]
    public void Convert_Money_ProducesTargetCurrency()
    {
        var converted = CurrencyConverter.Convert(new Money(100, "USD"), "JOD");
        converted.Should().NotBeNull();
        converted!.Value.Currency.Should().Be("JOD");
        converted.Value.Amount.Should().BeGreaterThan(0);
    }
}
