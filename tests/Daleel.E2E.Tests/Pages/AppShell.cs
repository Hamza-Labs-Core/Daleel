using Microsoft.Playwright;

namespace Daleel.E2E.Tests.Pages;

/// <summary>
/// The persistent app chrome: top app bar, language switcher, user menu and nav drawer. These
/// controls appear on every interactive (non-auth-layout) page.
/// </summary>
public sealed class AppShell : PageObject
{
    public AppShell(IPage page) : base(page) { }

    // The brand link in the app bar — a stable "the shell rendered" anchor.
    public ILocator BrandLink => Page.GetByRole(AriaRole.Link, new() { Name = "Daleel" });

    // LanguageSwitcher renders two MudButtons with hardcoded "EN" / "عربي" labels.
    public ILocator EnglishButton => Page.GetByRole(AriaRole.Button, new() { Name = "EN" }).First;
    public ILocator ArabicButton => Page.GetByRole(AriaRole.Button, new() { Name = "عربي" }).First;

    public ILocator MenuToggle => Page.Locator("button[aria-label='Menu'], .mud-appbar button").First;

    /// <summary>The avatar/sign-in control on the right of the app bar (UserMenu).</summary>
    public ILocator SignInButton => Page.GetByRole(AriaRole.Link, new() { Name = "Sign in" });

    /// <summary>
    /// Switch culture. The switcher POSTs to <c>/set-language</c> and force-reloads, so we wait for
    /// the full navigation to complete before returning.
    /// </summary>
    public Task SwitchToArabicAsync() =>
        Support.Nav.ClickAndAwaitNavigationAsync(Page, () => ArabicButton.ClickAsync());

    public Task SwitchToEnglishAsync() =>
        Support.Nav.ClickAndAwaitNavigationAsync(Page, () => EnglishButton.ClickAsync());

    /// <summary>Opens the user-menu dropdown (avatar) to reveal Account / Admin / Logout links.</summary>
    public async Task OpenUserMenuAsync()
    {
        // The avatar is a clickable activator inside the app bar's right cluster.
        await Page.Locator(".mud-appbar .mud-avatar, .mud-appbar [class*='mud-menu'] button").Last.ClickAsync();
    }

    public ILocator AdminMenuLink => Page.GetByRole(AriaRole.Link, new() { Name = "Admin" });
}
