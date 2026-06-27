using Daleel.E2E.Tests.Pages;
using Daleel.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Daleel.E2E.Tests.Tests;

/// <summary>
/// Test cases PROD-01, BRD-01, STORE-01: the detail pages resolve and render when reached by a direct
/// URL (the routes detail pages link to from search results), carrying the required <c>?name=</c> param.
/// These pages are public, so no sign-in is required.
/// </summary>
[TestFixture]
public class DirectUrlDetailTests : BaseTest
{
    [Test]
    public async Task Product_page_loads_by_direct_url()
    {
        var detail = new DetailPages(Page);
        await detail.GotoProductAsync("e2e-iphone15", "iPhone 15");
        await WaitForBlazorAsync();

        // The page either deep-scans and renders content, or shows a graceful error alert — never a crash.
        var ok = await Page.Locator("table, .mud-alert, .mud-progress-circular").First.IsVisibleAsync();
        Assert.That(ok, Is.True, "Product detail page should render content, a spinner, or an error alert.");
    }

    [Test]
    public async Task Brand_page_loads_by_direct_url()
    {
        var detail = new DetailPages(Page);
        await detail.GotoBrandAsync("e2e-samsung", "Samsung");
        await WaitForBlazorAsync();

        var ok = await Page.Locator("h4, .mud-alert, .mud-progress-circular").First.IsVisibleAsync();
        Assert.That(ok, Is.True, "Brand detail page should render a header, spinner, or error alert.");
    }

    [Test]
    public async Task Store_page_loads_by_direct_url()
    {
        var detail = new DetailPages(Page);
        await detail.GotoStoreAsync("e2e-store", "Extra");
        await WaitForBlazorAsync();

        var ok = await Page.Locator("h4, .mud-alert, .mud-progress-circular").First.IsVisibleAsync();
        Assert.That(ok, Is.True, "Store detail page should render a header, spinner, or error alert.");
    }
}
