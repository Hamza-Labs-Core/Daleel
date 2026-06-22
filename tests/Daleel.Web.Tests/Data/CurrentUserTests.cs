using System.Security.Claims;
using Daleel.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using Xunit;

namespace Daleel.Web.Tests.Data;

public class CurrentUserTests
{
    /// <summary>A stub provider that returns a fixed principal — stands in for the live circuit.</summary>
    private sealed class StubAuthStateProvider : AuthenticationStateProvider
    {
        private readonly ClaimsPrincipal _principal;
        public StubAuthStateProvider(ClaimsPrincipal principal) => _principal = principal;
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(_principal));
    }

    [Fact]
    public async Task GetUserId_ReturnsNameIdentifier_WhenAuthenticated()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "user-42"),
            new Claim("display_name", "Test User")
        }, authenticationType: "external"));
        var current = new CurrentUser(new StubAuthStateProvider(principal));

        (await current.IsAuthenticatedAsync()).Should().BeTrue();
        (await current.GetUserIdAsync()).Should().Be("user-42");
        (await current.GetDisplayNameAsync()).Should().Be("Test User");
    }

    [Fact]
    public async Task GetUserId_ReturnsNull_WhenAnonymous()
    {
        // No authentication type ⇒ IsAuthenticated is false.
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());
        var current = new CurrentUser(new StubAuthStateProvider(anonymous));

        (await current.IsAuthenticatedAsync()).Should().BeFalse();
        (await current.GetUserIdAsync()).Should().BeNull();
    }
}
