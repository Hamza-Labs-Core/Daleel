using Daleel.E2E.Tests.Pages;
using Microsoft.Playwright;

namespace Daleel.E2E.Tests.Support;

/// <summary>
/// Reusable account flows (register / sign-in / sign-out) over the static-SSR auth pages. Kept
/// separate from the page objects so tests can compose "arrange a user in this state" in one line.
/// </summary>
public static class Accounts
{
    /// <summary>
    /// Registers a brand-new throwaway user and leaves the page signed in as that user.
    /// Returns the generated email so the test can sign in again later.
    /// </summary>
    public static async Task<string> RegisterNewUserAsync(IPage page, string prefix = "e2e")
    {
        var email = TestConfig.UniqueEmail(prefix);
        var register = new RegisterPage(page);
        await register.GotoAsync();
        await register.RegisterAsync(email, TestConfig.DefaultPassword);
        return email;
    }

    /// <summary>Signs in an existing user via the login form.</summary>
    public static async Task SignInAsync(IPage page, string email, string password)
    {
        var login = new LoginPage(page);
        await login.GotoAsync();
        await login.SignInAsync(email, password);
    }

    /// <summary>Signs the current user out via the POST-only logout form.</summary>
    public static async Task SignOutAsync(IPage page)
    {
        await page.GotoAsync("/logout", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        var submit = page.Locator("form[action='/auth/logout'] button[type='submit']");
        await Nav.ClickAndAwaitNavigationAsync(page, () => submit.ClickAsync());
    }
}
