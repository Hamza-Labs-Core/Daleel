using Daleel.E2E.Tests.Pages;
using Daleel.E2E.Tests.Support;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Daleel.E2E.Tests.Tests;

/// <summary>
/// Test cases ADM-AC-03, ADM-DASH-01, ADM-USR-01/03/04: admin signs in, sees the dashboard KPIs,
/// opens the users page, and disables a user through the confirmation dialog.
///
/// <para>Admin identity is resolved two ways: if <c>E2E_ADMIN_EMAIL</c>/<c>E2E_ADMIN_PASSWORD</c> are
/// set we use them; otherwise we register a user, relying on Daleel's "first account is admin" rule —
/// valid only on a fresh database. If that account turns out not to be admin, the test self-skips.</para>
/// </summary>
[TestFixture]
public class AdminTests : BaseTest
{
    private async Task<bool> EnsureAdminSignedInAsync()
    {
        if (!string.IsNullOrWhiteSpace(TestConfig.AdminEmail) &&
            !string.IsNullOrWhiteSpace(TestConfig.AdminPassword))
        {
            await Accounts.SignInAsync(Page, TestConfig.AdminEmail!, TestConfig.AdminPassword!);
        }
        else
        {
            // First-user-is-admin on a fresh DB.
            await Accounts.RegisterNewUserAsync(Page, "admin");
        }

        // Confirm we actually have admin rights before asserting admin-only UI.
        var dashboard = new AdminDashboardPage(Page);
        await dashboard.GotoAsync();
        await WaitForBlazorAsync();

        var isAdmin = await dashboard.Heading.IsVisibleAsync();
        return isAdmin;
    }

    [Test]
    public async Task Admin_dashboard_shows_kpi_cards()
    {
        if (!await EnsureAdminSignedInAsync())
        {
            Assert.Ignore("Signed-in user is not an admin (DB not fresh and no E2E_ADMIN_* creds). Skipping.");
        }

        var dashboard = new AdminDashboardPage(Page);
        await Expect(dashboard.Heading).ToBeVisibleAsync();
        // At least one KPI card should be present.
        Assert.That(await dashboard.KpiCards.CountAsync(), Is.GreaterThan(0));
    }

    [Test]
    public async Task Admin_can_open_users_and_disable_a_user()
    {
        if (!await EnsureAdminSignedInAsync())
        {
            Assert.Ignore("Signed-in user is not an admin. Skipping.");
        }

        // Create a second, normal user (in an isolated session) so there is a row to disable.
        var victim = new IsolatedUser();
        await victim.CreateAsync(this);

        try
        {
            var users = new AdminUsersPage(Page);
            await users.GotoAsync();
            await WaitForBlazorAsync();
            await Expect(users.Table).ToBeVisibleAsync();

            // Narrow the table to the victim, then disable them.
            await users.SearchField.FillAsync(victim.Email);
            await users.FirstDisableToggle.ClickAsync();

            // The confirmation MessageBox appears; confirm it.
            await Expect(users.ConfirmDisableButton).ToBeVisibleAsync();
            await users.ConfirmDisableButton.ClickAsync();

            // A "disabled" chip and/or a success snackbar confirms the change.
            await Expect(users.DisabledChip.First).ToBeVisibleAsync(new() { Timeout = 15_000 });
        }
        finally
        {
            await victim.DisposeAsync();
        }
    }

    /// <summary>Holds an isolated browser context used to register a throwaway normal user.</summary>
    private sealed class IsolatedUser
    {
        private IBrowserContext? _context;
        public string Email { get; private set; } = string.Empty;

        public async Task CreateAsync(AdminTests test)
        {
            _context = await test.NewIsolatedContextAsync();
            var page = await _context.NewPageAsync();
            Email = await Accounts.RegisterNewUserAsync(page, "victim");
        }

        public async Task DisposeAsync()
        {
            if (_context is not null)
            {
                await _context.CloseAsync();
            }
        }
    }
}
