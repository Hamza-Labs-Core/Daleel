using Daleel.E2E.Tests.Pages;
using Daleel.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Daleel.E2E.Tests.Tests;

/// <summary>
/// Test cases LAND-05/07, X-01: the language toggle switches the whole document between English (LTR)
/// and Arabic (RTL) and back, and visible text changes accordingly.
/// </summary>
[TestFixture]
public class LanguageToggleTests : BaseTest
{
    [Test]
    public async Task Toggle_switches_direction_EN_to_AR_and_back()
    {
        var landing = new LandingPage(Page);
        await landing.GotoAsync();
        Assert.That(await GetDirectionAsync(), Is.EqualTo("ltr"));

        var shell = new AppShell(Page);
        await shell.SwitchToArabicAsync();
        Assert.That(await GetDirectionAsync(), Is.EqualTo("rtl"), "Arabic should render right-to-left.");

        await shell.SwitchToEnglishAsync();
        Assert.That(await GetDirectionAsync(), Is.EqualTo("ltr"), "English should render left-to-right.");
    }

    [Test]
    public async Task Toggle_changes_visible_text_on_login_page()
    {
        var login = new LoginPage(Page);
        await login.GotoAsync();
        var englishBody = await Page.Locator("body").InnerTextAsync();

        // Switch culture from a page that always carries the switcher (MainLayout), since the auth
        // layout may not. The culture cookie then applies to /login on the next navigation.
        var landing = new LandingPage(Page);
        await landing.GotoAsync();
        var shell = new AppShell(Page);
        await shell.SwitchToArabicAsync();

        await login.GotoAsync();
        var arabicBody = await Page.Locator("body").InnerTextAsync();

        Assert.That(arabicBody, Is.Not.EqualTo(englishBody),
            "Switching to Arabic should change the visible page text.");
    }
}
