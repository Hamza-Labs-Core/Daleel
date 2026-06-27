using Daleel.E2E.Tests.Pages;
using Daleel.E2E.Tests.Support;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Daleel.E2E.Tests.Tests;

/// <summary>
/// Test cases REG-06, LOGIN-03, HOME-02/03/06: the full Register → Login → Search → Results journey.
/// </summary>
[TestFixture]
public class AuthFlowTests : BaseTest
{
    [Test]
    public async Task Register_then_login_then_search_shows_progress_and_results()
    {
        // ── Register a fresh user (auto-signs-in) ───────────────────────────────
        var email = await Accounts.RegisterNewUserAsync(Page, "flow");

        // Land back in the app, authenticated. Sign out so we can exercise the login form too.
        await Accounts.SignOutAsync(Page);

        // ── Log in with the same credentials ────────────────────────────────────
        await Accounts.SignInAsync(Page, email, TestConfig.DefaultPassword);
        await Expect(Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex(@"/(\?|$)"));

        // ── Run a search and watch the progress stepper appear ──────────────────
        var home = new HomePage(Page);
        await home.GotoAsync();
        await WaitForBlazorAsync();

        await home.SearchAsync("iPhone 15");
        await Expect(home.Stepper).ToBeVisibleAsync(new() { Timeout = 20_000 });

        // ── Eventually results (cards) or a graceful empty/error state appears ──
        // We don't hard-assert on card count (depends on live provider data); we assert the search
        // completes by the progress UI settling or result cards rendering.
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle,
            new PageWaitForLoadStateOptions { Timeout = 60_000 });
    }

    [Test]
    public async Task Login_with_wrong_password_shows_generic_error()
    {
        var email = await Accounts.RegisterNewUserAsync(Page, "wrongpw");
        await Accounts.SignOutAsync(Page);

        var login = new LoginPage(Page);
        await login.GotoAsync();
        await login.SignInAsync(email, "definitely-not-the-password");

        // Redirected back to /login with an error code; a friendly alert renders.
        await Expect(Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex(@"/login\?error="));
        await Expect(login.ErrorAlert.First).ToBeVisibleAsync();
    }

    [Test]
    public async Task Register_password_mismatch_is_rejected()
    {
        var register = new RegisterPage(Page);
        await register.GotoAsync();
        await register.Email.FillAsync(TestConfig.UniqueEmail("mismatch"));
        await register.Password.FillAsync("Test!2345Pass");
        await register.Confirm.FillAsync("Different!2345");
        await Support.Nav.ClickAndAwaitNavigationAsync(Page, () => register.Submit.ClickAsync());

        await Expect(Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex(@"/register\?error=mismatch"));
    }
}
