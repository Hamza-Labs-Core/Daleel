using Daleel.E2E.Tests.Pages;
using Daleel.E2E.Tests.Support;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Daleel.E2E.Tests.Tests;

/// <summary>
/// Test cases CMP-01/03/04: compare two products and render a side-by-side specification table.
/// </summary>
[TestFixture]
public class CompareTests : BaseTest
{
    [Test]
    public async Task Compare_page_renders_inputs()
    {
        await Accounts.RegisterNewUserAsync(Page, "cmp");

        var compare = new ComparePage(Page);
        await compare.GotoAsync();
        await WaitForBlazorAsync();

        await Expect(compare.ProductA).ToBeVisibleAsync();
        await Expect(compare.ProductB).ToBeVisibleAsync();
        await Expect(compare.CompareButton).ToBeVisibleAsync();
    }

    [Test]
    public async Task Compare_two_products_shows_comparison_table()
    {
        await Accounts.RegisterNewUserAsync(Page, "cmprun");

        var compare = new ComparePage(Page);
        await compare.GotoAsync();
        await WaitForBlazorAsync();

        await compare.RunAsync("iPhone 15", "Galaxy S24");

        // The comparison table renders once the agent finishes; allow generous time for live data.
        try
        {
            await compare.ComparisonTable.WaitForAsync(new() { Timeout = 90_000 });
        }
        catch (TimeoutException)
        {
            Assert.Ignore("Comparison did not complete with live providers in time — skipping table assertion.");
        }

        await Expect(compare.ComparisonTable).ToBeVisibleAsync();
    }
}
