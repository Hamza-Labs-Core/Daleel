using System.Linq;
using Bunit;
using Bunit.TestDoubles;
using Daleel.Web.Components.Shared;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Daleel.Web.Tests;

/// <summary>
/// Guards the first-login antiforgery fix. <see cref="RedirectToLogin"/> is the <c>[Authorize]</c> gate
/// (rendered by Routes.razor's NotAuthorized) and the dominant way users reach <c>/login</c>. It MUST
/// send the browser to <c>/login</c> with a full-page load (<c>forceLoad: true</c>): the login form's
/// antiforgery <c>Set-Cookie</c> is emitted on the same response that renders the form, and a Blazor
/// enhanced-navigation (fetch) response does not reliably commit that cookie to the jar (notably
/// iOS Safari). Without the cookie, the very first POST to /auth/login fails antiforgery validation —
/// the "Your session expired before the form was submitted" error that only clears on the second try.
/// </summary>
public class RedirectToLoginTests : TestContext
{
    [Fact]
    public void RedirectToLogin_NavigatesToLogin_WithForceLoad_PreservingReturnUrl()
    {
        var nav = (FakeNavigationManager)Services.GetRequiredService<NavigationManager>();
        // Simulate landing on a protected page the user was actually trying to reach.
        nav.NavigateTo("http://localhost/history?tab=saved");

        RenderComponent<RedirectToLogin>();

        // bUnit's FakeNavigationManager records history newest-first, so the component's redirect is first.
        var redirect = nav.History.First();
        redirect.Uri.Should().StartWith("/login?returnUrl=", "the redirect must target /login and carry the return path");
        redirect.Uri.Should().Contain(Uri.EscapeDataString("/history?tab=saved"));
        redirect.Options.ForceLoad.Should().BeTrue(
            "enhanced navigation (fetch) drops the antiforgery Set-Cookie, so /login must be reached " +
            "via a full-page load for the first login POST to succeed");
    }
}
