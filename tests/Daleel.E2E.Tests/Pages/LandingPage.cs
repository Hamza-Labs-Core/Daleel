using Microsoft.Playwright;

namespace Daleel.E2E.Tests.Pages;

/// <summary>
/// The marketing landing page shown at <c>/</c> to anonymous visitors (<c>LandingPage.razor</c>).
/// </summary>
public sealed class LandingPage : PageObject
{
    public LandingPage(IPage page) : base(page) { }

    public ILocator Hero => Page.Locator(".daleel-landing-hero");
    public ILocator Title => Page.Locator(".daleel-landing-title");
    public ILocator FeatureCards => Page.Locator(".daleel-feature-card");

    public ILocator GetStartedCta => Page.GetByRole(AriaRole.Link, new() { Name = "Get Started" });
    public ILocator SignInCta => Page.GetByRole(AriaRole.Link, new() { Name = "Sign In" });

    public Task GotoAsync() =>
        Page.GotoAsync("/", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
}
