using Daleel.Agent;
using FluentAssertions;
using Xunit;

namespace Daleel.Agent.Tests;

/// <summary>
/// Pins the page-title/listing-url gates: search/category/SEO pages must never become product
/// cards. Every positive case below was observed as a live grid card.
/// </summary>
public class PageTitleGateTests
{
    [Theory]
    [InlineData("Espresso Machine in Jordan | Find the Lowest Prices")]
    [InlineData("ماكينة اسبريسو في الأردن | اقل الاسعار في الممكة")]
    [InlineData("Best prices on espresso makers in Amman")]
    public void Seo_titles_are_not_products(string title) =>
        AgentService.LooksLikePageTitle(title).Should().BeTrue();

    [Theory]
    [InlineData("DeLonghi Espresso Maker Dedica Style 15 Bar")]
    [InlineData("ماكينه قهوه اسبريسو ويجا ايطالي - السوق المفتوح")] // a real opensooq AD, keep it
    [InlineData("Beko Espresso Machine 15 Bar 1628W")]
    public void Real_product_names_pass(string title) =>
        AgentService.LooksLikePageTitle(title).Should().BeFalse();

    [Theory]
    [InlineData("https://souqprice.com/jo/en-jo/product/search?sort=rating&search=espresso%20machine")]
    [InlineData("https://leaders.jo/en/product-category/kitchen-appliances/coffee-and-espresso-machines/")]
    [InlineData("https://jo-cell.com/collections/espresso-machines")]
    public void Listing_pages_are_not_product_urls(string url) =>
        AgentService.IsListingPageUrl(url).Should().BeTrue();

    [Theory]
    [InlineData("https://jo-cell.com/products/delonghi-espresso-maker-dedica-style")]
    [InlineData("https://leaders.jo/en/product/beko-espresso-machine-15-bar-1628w/")]
    [InlineData("https://jo.opensooq.com/ar/search/270931063")] // opensooq AD urls live under /search/<id>
    public void Product_pages_pass(string url) =>
        AgentService.IsListingPageUrl(url).Should().BeFalse();
}
