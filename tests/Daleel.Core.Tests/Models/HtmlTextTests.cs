using Daleel.Core.Models;
using Daleel.Core.Pricing;
using Xunit;

namespace Daleel.Core.Tests.Models;

/// <summary>
/// Pins the HTML-leak fix: scraped markup must never surface as product text. The first case is
/// the live find — a Next.js loading skeleton rendered as a product NAME on the grid.
/// </summary>
public class HtmlTextTests
{
    [Fact]
    public void Nextjs_loading_skeleton_strips_to_nothing_meaningful()
    {
        var live = "</style><div class=\"h-screen flex justify-center items-center\">" +
                   "<img src=\"/_next/static/media/phc-logo.d8d2de46.svg\" alt=\"Loading\" class=\"w-10\">";
        Assert.DoesNotContain("<", HtmlText.Strip(live));
        Assert.DoesNotContain("class=", HtmlText.Strip(live));
    }

    [Theory]
    [InlineData("Beko <b>Espresso</b> Machine &amp; Frother", "Beko Espresso Machine & Frother")]
    [InlineData("Plain product name 15 Bar", "Plain product name 15 Bar")]
    [InlineData(null, null)]
    public void Tags_and_entities_resolve_text_passes_through(string? input, string? expected) =>
        Assert.Equal(expected, HtmlText.Strip(input));

    [Fact]
    public void Price_parser_lines_carry_no_markup()
    {
        var page = "<div class=\"price\"><span>Samsung WindFree AC</span> 499.00 JOD</div>";
        var match = Assert.Single(PriceParser.Extract(page));
        Assert.DoesNotContain("<", match.Line);
        Assert.Equal(499.00m, match.Price);
    }
}
