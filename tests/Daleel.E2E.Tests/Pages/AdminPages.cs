using Microsoft.Playwright;

namespace Daleel.E2E.Tests.Pages;

/// <summary>
/// Admin dashboard at <c>/admin</c> (<c>AdminDashboard.razor</c>) plus the shared <c>AdminNav</c>
/// tab strip linking to every admin page.
/// </summary>
public sealed class AdminDashboardPage : PageObject
{
    public AdminDashboardPage(IPage page) : base(page) { }

    public Task GotoAsync() =>
        Page.GotoAsync("/admin", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

    public ILocator Heading => Page.GetByRole(AriaRole.Heading, new() { Name = "Dashboard" });
    public ILocator KpiCards => Page.Locator(".mud-paper").Filter(new() { Has = Page.Locator(".mud-typography") });
    public ILocator UsersNav => Page.GetByRole(AriaRole.Link, new() { Name = "Users" });
    public ILocator UsageNav => Page.GetByRole(AriaRole.Link, new() { Name = "Usage" });
}

/// <summary>
/// Admin users page at <c>/admin/users</c> (<c>AdminUsers.razor</c>): a MudTable of users with
/// per-row disable/enable and grant/revoke-admin actions, each gated by a confirmation MessageBox.
/// </summary>
public sealed class AdminUsersPage : PageObject
{
    public AdminUsersPage(IPage page) : base(page) { }

    public Task GotoAsync() =>
        Page.GotoAsync("/admin/users", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

    public ILocator Table => Page.Locator(".mud-table");
    public ILocator Rows => Page.Locator(".mud-table-body tr");
    public ILocator SearchField => Page.GetByPlaceholder("Search name/email…");

    // The disable/enable toggle is an icon button with a "Disable"/"Enable" tooltip/aria-label.
    public ILocator FirstDisableToggle =>
        Page.Locator("button[aria-label='Disable'], button[title='Disable']").First;
    public ILocator FirstEnableToggle =>
        Page.Locator("button[aria-label='Enable'], button[title='Enable']").First;

    // The confirmation dialog buttons (MudMessageBox).
    public ILocator ConfirmDisableButton => Page.GetByRole(AriaRole.Button, new() { Name = "Disable" }).Last;
    public ILocator ConfirmEnableButton => Page.GetByRole(AriaRole.Button, new() { Name = "Enable" }).Last;
    public ILocator CancelDialogButton => Page.GetByRole(AriaRole.Button, new() { Name = "Cancel" });
    public ILocator DisabledChip => Page.Locator(".mud-chip").Filter(new() { HasText = "disabled" });
    public ILocator Snackbar => Page.Locator(".mud-snackbar");
}

/// <summary>
/// Admin usage page at <c>/admin/usage</c> (<c>AdminUsage.razor</c>): KPI cards, a sortable
/// provider <c>MudDataGrid</c>, category chips, a recent-events grid, and a period toggle.
/// </summary>
public sealed class AdminUsagePage : PageObject
{
    public AdminUsagePage(IPage page) : base(page) { }

    public Task GotoAsync() =>
        Page.GotoAsync("/admin/usage", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

    public ILocator KpiCards => Page.Locator(".mud-paper");
    public ILocator DataGrids => Page.Locator(".mud-table, .mud-data-grid");
    public ILocator CategoryChips => Page.Locator(".mud-chip");
    public ILocator PeriodToggle => Page.Locator(".mud-toggle-group");
    public ILocator NotConfiguredAlert => Page.Locator(".mud-alert");

    /// <summary>Clicks a period in the today/week/month/all toggle group.</summary>
    public Task SelectPeriodAsync(string label) =>
        PeriodToggle.GetByText(label, new() { Exact = true }).ClickAsync();
}
