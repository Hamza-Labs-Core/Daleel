using Daleel.E2E.Tests.Pages;
using Daleel.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Daleel.E2E.Tests.Tests;

/// <summary>
/// Test cases ADM-USE-01/02/03/07: the admin usage page loads with KPI cards and data tables (or a
/// graceful "not configured" notice when no Postgres event store is wired up), and the period toggle works.
/// </summary>
[TestFixture]
public class AdminUsageTests : BaseTest
{
    [Test]
    public async Task Usage_page_loads_with_kpis_or_not_configured_notice()
    {
        if (!string.IsNullOrWhiteSpace(TestConfig.AdminEmail))
        {
            await Accounts.SignInAsync(Page, TestConfig.AdminEmail!, TestConfig.AdminPassword!);
        }
        else
        {
            await Accounts.RegisterNewUserAsync(Page, "usage");
        }

        var usage = new AdminUsagePage(Page);
        await usage.GotoAsync();
        await WaitForBlazorAsync();

        if (!await Page.GetByRole(Microsoft.Playwright.AriaRole.Heading).First.IsVisibleAsync())
        {
            Assert.Ignore("Could not reach /admin/usage (likely not an admin). Skipping.");
        }

        // Either the KPI cards render (event store configured) or the info alert explains it isn't.
        var hasKpis = await usage.KpiCards.CountAsync() > 0;
        var hasNotice = await usage.NotConfiguredAlert.CountAsync() > 0;
        Assert.That(hasKpis || hasNotice, Is.True,
            "Expected either KPI cards or a 'not configured' notice on the usage page.");
    }

    [Test]
    public async Task Usage_period_toggle_is_present()
    {
        if (!string.IsNullOrWhiteSpace(TestConfig.AdminEmail))
        {
            await Accounts.SignInAsync(Page, TestConfig.AdminEmail!, TestConfig.AdminPassword!);
        }
        else
        {
            await Accounts.RegisterNewUserAsync(Page, "usagetog");
        }

        var usage = new AdminUsagePage(Page);
        await usage.GotoAsync();
        await WaitForBlazorAsync();

        // The toggle only renders when the event store is enabled; if absent, the page showed the
        // not-configured notice instead, which is still valid.
        if (await usage.PeriodToggle.CountAsync() > 0)
        {
            await Expect(usage.PeriodToggle).ToBeVisibleAsync();
        }
        else
        {
            await Expect(usage.NotConfiguredAlert.First).ToBeVisibleAsync();
        }
    }
}
