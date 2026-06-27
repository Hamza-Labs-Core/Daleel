using Microsoft.Playwright;

namespace Daleel.E2E.Tests.Pages;

/// <summary>
/// Base for all page objects. A page object wraps a single page/feature and exposes intent-revealing
/// locators and actions, so tests read as behaviour ("login as admin", "open product detail") and
/// selector churn is contained to one place.
/// </summary>
public abstract class PageObject
{
    protected readonly IPage Page;

    protected PageObject(IPage page) => Page = page;
}
