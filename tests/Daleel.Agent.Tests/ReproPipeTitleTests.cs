using Daleel.Agent;
using Daleel.Core.Intelligence;
using FluentAssertions;
using Xunit;

namespace Daleel.Agent.Tests;

/// <summary>
/// Regression: the page-title gate must not drop real product pages that append "| StoreName" —
/// a bare pipe once flagged "Beko Espresso Machine 15 Bar 1628W | Leaders Center" as an SEO page
/// (branch review, matching-quality). A pipe is a page-title signal only when nothing before it
/// carries a product-identity token (a digit). SEO/query-echo titles still get dropped.
/// </summary>
public class ReproPipeTitleTests
{
    [Theory]
    [InlineData("De'Longhi Dedica EC685 | SmartBuy Jordan")]            // EC685 → identity
    [InlineData("Beko Espresso Machine 15 Bar 1628W | Leaders Center")] // 15/1628W → identity
    public void Pipe_separated_real_product_titles_survive(string title) =>
        AgentService.LooksLikePageTitle(title).Should().BeFalse();

    [Theory]
    [InlineData("Espresso Machine in Jordan | Find the answers")]       // no digit before pipe → page
    [InlineData("Coffee Machines | Souqprice")]
    [InlineData("Espresso Machine in Jordan | Find the Lowest Prices")] // + price phrase
    public void Seo_and_category_titles_are_dropped(string title) =>
        AgentService.LooksLikePageTitle(title).Should().BeTrue();

    [Fact]
    public void Pipe_titled_product_page_reaches_the_gate_and_now_passes_it()
    {
        var type = ResultClassifier.Classify(
            "https://www.smartbuy-me.com/product/delonghi-dedica-ec685",
            "De'Longhi Dedica EC685 | SmartBuy Jordan",
            "Espresso machine with 15 bar pump.");
        type.Should().Be(ResultType.ProductListing);

        AgentService.LooksLikePageTitle("De'Longhi Dedica EC685 | SmartBuy Jordan").Should().BeFalse();
        AgentService.IsListingPageUrl("https://www.smartbuy-me.com/product/delonghi-dedica-ec685").Should().BeFalse();
    }
}
