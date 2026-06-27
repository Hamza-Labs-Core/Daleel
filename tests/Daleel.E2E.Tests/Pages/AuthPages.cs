using Microsoft.Playwright;

namespace Daleel.E2E.Tests.Pages;

/// <summary>
/// The static-SSR login page (<c>/login</c>). It is a plain HTML form that POSTs to
/// <c>/auth/login</c> with an antiforgery token — NOT a Blazor circuit — so we fill native inputs
/// by their stable ids and submit, then follow the 302 redirect.
/// </summary>
public sealed class LoginPage : PageObject
{
    public LoginPage(IPage page) : base(page) { }

    public ILocator Email => Page.Locator("#login-email");
    public ILocator Password => Page.Locator("#login-password");
    public ILocator Submit => Page.Locator("form[action='/auth/login'] button[type='submit']");
    public ILocator ErrorAlert => Page.Locator(".mud-alert");

    public Task GotoAsync(string? returnUrl = null)
    {
        var path = returnUrl is null ? "/login" : $"/login?returnUrl={Uri.EscapeDataString(returnUrl)}";
        return Page.GotoAsync(path, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
    }

    /// <summary>Fills credentials, submits, and waits for the post-login navigation to settle.</summary>
    public async Task SignInAsync(string email, string password)
    {
        await Email.FillAsync(email);
        await Password.FillAsync(password);
        await Support.Nav.ClickAndAwaitNavigationAsync(Page, () => Submit.ClickAsync());
    }
}

/// <summary>
/// The static-SSR registration page (<c>/register</c>) — same model as login, POSTing to
/// <c>/auth/register</c>. The first account ever created on a fresh DB is promoted to admin.
/// </summary>
public sealed class RegisterPage : PageObject
{
    public RegisterPage(IPage page) : base(page) { }

    public ILocator Email => Page.Locator("#reg-email");
    public ILocator Password => Page.Locator("#reg-password");
    public ILocator Confirm => Page.Locator("#reg-confirm");
    public ILocator Submit => Page.Locator("form[action='/auth/register'] button[type='submit']");
    public ILocator ErrorAlert => Page.Locator(".mud-alert");

    public Task GotoAsync() =>
        Page.GotoAsync("/register", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

    /// <summary>Registers a new account; on success the user is auto-signed-in and redirected.</summary>
    public async Task RegisterAsync(string email, string password)
    {
        await Email.FillAsync(email);
        await Password.FillAsync(password);
        await Confirm.FillAsync(password);
        await Support.Nav.ClickAndAwaitNavigationAsync(Page, () => Submit.ClickAsync());
    }
}
