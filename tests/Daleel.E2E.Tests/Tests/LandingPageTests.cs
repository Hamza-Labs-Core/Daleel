using Daleel.E2E.Tests.Pages;
using Daleel.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Daleel.E2E.Tests.Tests;

/// <summary>
/// Test case LAND-01/05/06: the public landing page renders in English (LTR) and Arabic (RTL).
/// </summary>
[TestFixture]
public class LandingPageTests : BaseTest
{
    [Test]
    public async Task Landing_loads_in_English_LTR()
    {
        var landing = new LandingPage(Page);
        await landing.GotoAsync();

        await Expect(landing.Hero).ToBeVisibleAsync();
        await Expect(landing.GetStartedCta).ToBeVisibleAsync();
        await Expect(landing.SignInCta).ToBeVisibleAsync();

        Assert.That(await GetDirectionAsync(), Is.EqualTo("ltr"));
    }

    [Test]
    public async Task Landing_loads_in_Arabic_RTL()
    {
        var landing = new LandingPage(Page);
        await landing.GotoAsync();

        var shell = new AppShell(Page);
        await shell.SwitchToArabicAsync();

        // After the culture switch + reload, the document flips to right-to-left.
        Assert.That(await GetDirectionAsync(), Is.EqualTo("rtl"));
        await Expect(landing.Hero).ToBeVisibleAsync();
    }

    [Test]
    public async Task Get_Started_navigates_to_register()
    {
        var landing = new LandingPage(Page);
        await landing.GotoAsync();

        await landing.GetStartedCta.ClickAsync();
        await Expect(Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/register"));
    }
}
