using Microsoft.Playwright;

namespace Daleel.E2E.Tests.Pages;

/// <summary>
/// Product results grid + the product detail dialog (<c>ProductListings.razor</c>,
/// <c>ModelDetailDialog.razor</c>). The dialog is the concise popup; "View Full Details" routes to
/// the full <c>/product/{id}</c> page.
/// </summary>
public sealed class ProductResults : PageObject
{
    public ProductResults(IPage page) : base(page) { }

    public ILocator ModelCards => Page.Locator(".mud-card");
    public ILocator DetailsButtons => Page.GetByRole(AriaRole.Button, new() { Name = "Details" });
    public ILocator CompareButton => Page.GetByRole(AriaRole.Button, new() { Name = "Compare" });
    public ILocator CompareCheckboxes => Page.Locator(".mud-card input[type='checkbox']");

    // The detail dialog.
    public ILocator Dialog => Page.Locator(".mud-dialog").Last;
    public ILocator ViewFullDetailsButton => Page.GetByRole(AriaRole.Button, new() { Name = "View Full Details" });
    public ILocator OfferTable => Dialog.Locator("table");
    public ILocator DialogCloseButton => Dialog.GetByRole(AriaRole.Button, new() { Name = "Close" });

    /// <summary>Opens the concise detail dialog for the first result card.</summary>
    public async Task OpenFirstDetailAsync()
    {
        await DetailsButtons.First.ClickAsync();
        await Dialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
    }
}

/// <summary>
/// The interactive comparison page at <c>/compare</c> (<c>Compare.razor</c>): two product fields,
/// a Compare button, then a side-by-side <c>ComparisonTable</c> with winner highlighting.
/// </summary>
public sealed class ComparePage : PageObject
{
    public ComparePage(IPage page) : base(page) { }

    public Task GotoAsync(string? a = null, string? b = null)
    {
        var path = "/compare";
        if (a is not null && b is not null)
        {
            path += $"?q={Uri.EscapeDataString($"{a} vs {b}")}";
        }

        return Page.GotoAsync(path, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
    }

    // Two MudTextFields rendered in document order.
    public ILocator ProductA => Page.Locator(".mud-input-control input").Nth(0);
    public ILocator ProductB => Page.Locator(".mud-input-control input").Nth(1);
    public ILocator CompareButton => Page.GetByRole(AriaRole.Button, new() { Name = "Compare" });

    public ILocator ComparisonTable => Page.Locator("table").First;
    public ILocator WinnerChip => Page.Locator(".mud-chip").Filter(new() { HasText = "Winner" }).First;

    public async Task RunAsync(string a, string b)
    {
        await ProductA.FillAsync(a);
        await ProductB.FillAsync(b);
        await CompareButton.ClickAsync();
    }
}

/// <summary>
/// Direct-URL detail pages: <c>/product/{id}</c>, <c>/brand/{id}</c>, <c>/store/{id}</c>. Each needs
/// a <c>?name=</c> query param to re-resolve the entity.
/// </summary>
public sealed class DetailPages : PageObject
{
    public DetailPages(IPage page) : base(page) { }

    public Task GotoProductAsync(string id, string name) =>
        Page.GotoAsync($"/product/{id}?name={Uri.EscapeDataString(name)}",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

    public Task GotoBrandAsync(string id, string name) =>
        Page.GotoAsync($"/brand/{id}?name={Uri.EscapeDataString(name)}",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

    public Task GotoStoreAsync(string id, string name) =>
        Page.GotoAsync($"/store/{id}?name={Uri.EscapeDataString(name)}",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

    public ILocator BackButton => Page.GetByRole(AriaRole.Button, new() { Name = "Back" });
    public ILocator ErrorAlert => Page.Locator(".mud-alert");
    public ILocator SpecsTable => Page.Locator("table");
    public ILocator MapsButton => Page.GetByRole(AriaRole.Link, new() { Name = "Maps" });
}
