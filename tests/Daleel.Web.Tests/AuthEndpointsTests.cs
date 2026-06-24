using System.Reflection;
using Daleel.Web;
using Daleel.Web.Auth;
using Daleel.Web.Components.Pages;
using FluentAssertions;
using Xunit;

namespace Daleel.Web.Tests;

/// <summary>
/// Guards the login-crash fix: the open-redirect coercion used by the auth endpoints, and the
/// [StaticSsrPage] wiring that makes App.razor render the auth pages statically (replacing the fragile
/// Request.Path check that crashed login behind Caddy with "Headers are read-only").
/// </summary>
public class AuthEndpointsTests
{
    [Theory]
    [InlineData("/history", "/history")]                 // ordinary local path — preserved
    [InlineData("/history?tab=saved", "/history?tab=saved")] // query string preserved
    [InlineData("/", "/")]                               // root preserved
    public void SafeLocalPath_PreservesLocalPaths(string input, string expected)
        => AuthEndpoints.SafeLocalPath(input).Should().Be(expected);

    [Theory]
    [InlineData(null)]                  // missing
    [InlineData("")]                    // empty
    [InlineData("   ")]                 // whitespace
    [InlineData("https://evil.com")]    // absolute URL — open redirect
    [InlineData("http://evil.com")]     // absolute URL — open redirect
    [InlineData("//evil.com")]          // protocol-relative — open redirect
    [InlineData("/\\evil.com")]         // back-slash trick — open redirect
    [InlineData("javascript:alert(1)")] // not a local path
    [InlineData("evil.com")]            // bare host — not a local path
    public void SafeLocalPath_RejectsUnsafeTargets_FallingBackToRoot(string? input)
        => AuthEndpoints.SafeLocalPath(input).Should().Be("/");

    [Theory]
    [InlineData(typeof(Login))]
    [InlineData(typeof(Register))]
    [InlineData(typeof(Logout))]
    public void AuthPages_AreFlaggedStaticSsr(Type pageType)
        => pageType.IsDefined(typeof(StaticSsrPageAttribute), inherit: false)
            .Should().BeTrue($"{pageType.Name} writes/clears the auth cookie via a form post and must render static SSR");

    [Fact]
    public void OrdinaryInteractivePage_IsNotFlaggedStaticSsr()
        // Home is a normal interactive page — App.razor must give it InteractiveServer, not static SSR.
        => typeof(Home).IsDefined(typeof(StaticSsrPageAttribute), inherit: false).Should().BeFalse();
}
