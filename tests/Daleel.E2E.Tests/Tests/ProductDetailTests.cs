using Daleel.E2E.Tests.Pages;
using Daleel.E2E.Tests.Support;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Daleel.E2E.Tests.Tests;

/// <summary>
/// Test cases RES-11, PDLG-01/04: the product detail popup opens with data and links to the full page.
/// </summary>
[TestFixture]
public class ProductDetailTests : BaseTest
{
    [Test]
    public async Task Detail_popup_opens_and_shows_offer_data()
    {
        await Accounts.RegisterNewUserAsync(Page, "pdlg");

        var home = new HomePage(Page);
        await home.GotoAsync();
        await WaitForBlazorAsync();
        await home.SearchAsync("Samsung Galaxy S24");

        // Wait for results to materialize, then open the first card's detail dialog.
        var results = new ProductResults(Page);
        try
        {
            await results.DetailsButtons.First.WaitForAsync(new() { Timeout = 60_000 });
        }
        catch (TimeoutException)
        {
            Assert.Ignore("No product results returned by the live providers for this query — skipping popup assertions.");
        }

        await results.OpenFirstDetailAsync();

        await Expect(results.Dialog).ToBeVisibleAsync();
        await Expect(results.ViewFullDetailsButton).ToBeVisibleAsync();
        // The concise view always shows the "where to buy" offer table.
        await Expect(results.OfferTable.First).ToBeVisibleAsync();
    }

    [Test]
    public async Task View_full_details_navigates_to_product_page()
    {
        await Accounts.RegisterNewUserAsync(Page, "pfull");

        var home = new HomePage(Page);
        await home.GotoAsync();
        await WaitForBlazorAsync();
        await home.SearchAsync("Sony WH-1000XM5");

        var results = new ProductResults(Page);
        try
        {
            await results.DetailsButtons.First.WaitForAsync(new() { Timeout = 60_000 });
        }
        catch (TimeoutException)
        {
            Assert.Ignore("No product results returned — skipping navigation assertion.");
        }

        await results.OpenFirstDetailAsync();
        await results.ViewFullDetailsButton.ClickAsync();

        await Expect(Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/product/"));
    }
}
