using Daleel.E2E.Tests.Pages;
using Daleel.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Daleel.E2E.Tests.Tests;

/// <summary>
/// Test cases HOME-03/04: the animated SearchProgress stepper (pulse dot, 8-step strip, progress bar)
/// appears while a search is running.
/// </summary>
[TestFixture]
public class SearchProgressTests : BaseTest
{
    [Test]
    public async Task Progress_stepper_appears_during_search()
    {
        await Accounts.RegisterNewUserAsync(Page, "prog");

        var home = new HomePage(Page);
        await home.GotoAsync();
        await WaitForBlazorAsync();

        await home.SearchAsync("best 4k tv");

        // The horizontal stepper and progress bar are the visible signature of a live search.
        await Expect(home.Stepper).ToBeVisibleAsync(new() { Timeout = 20_000 });
        await Expect(home.ProgressBar).ToBeVisibleAsync();

        // The stepper exposes its discrete steps as .daleel-step elements.
        Assert.That(await home.Steps.CountAsync(), Is.GreaterThan(0));
    }
}
