using Microsoft.Playwright;

namespace Daleel.E2E.Tests.Pages;

/// <summary>
/// The authenticated search workspace at <c>/</c> (<c>Home.razor</c>): a sticky chat input, the
/// animated <c>SearchProgress</c> stepper while a search runs, and the results grid.
/// </summary>
public sealed class HomePage : PageObject
{
    public HomePage(IPage page) : base(page) { }

    public Task GotoAsync(string? query = null)
    {
        var path = query is null ? "/" : $"/?q={Uri.EscapeDataString(query)}";
        return Page.GotoAsync(path, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
    }

    // The chat-style search box (MudTextField → a textarea/input) and its send button.
    public ILocator SearchInput => Page.Locator(".mud-input-control textarea, .mud-input-control input[type='text']").Last;
    public ILocator SendButton => Page.Locator("button[aria-label*='end' i], .mud-icon-button").Last;

    // SearchProgress component hooks. A single always-visible vertical stepper (.daleel-stepper-v),
    // whose discrete stages are .daleel-step-v rows — no text feed, nothing behind an expander.
    public ILocator ProgressPulse => Page.Locator(".daleel-status-pulse");
    public ILocator Stepper => Page.Locator(".daleel-stepper-v");
    public ILocator Steps => Page.Locator(".daleel-step-v");
    public ILocator ProgressBar => Page.Locator(".mud-progress-linear").First;
    public ILocator CancelButton => Page.GetByRole(AriaRole.Button, new() { Name = "Cancel" });

    // Results.
    public ILocator ResultCards => Page.Locator(".mud-card");
    // Recent searches now live in the left-nav "Recent" submenu, not on the page.
    public ILocator RecentNav => Page.GetByText("Recent");

    /// <summary>Types a query into the chat box and submits it with Enter.</summary>
    public async Task SearchAsync(string query)
    {
        await SearchInput.FillAsync(query);
        await SearchInput.PressAsync("Enter");
    }

    /// <summary>Waits for the live search progress stepper to appear (proof the search started).</summary>
    public Task WaitForProgressAsync() =>
        Stepper.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
}
