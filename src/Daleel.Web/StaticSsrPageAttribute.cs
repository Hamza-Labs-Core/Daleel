namespace Daleel.Web;

/// <summary>
/// Marks a routable page component that MUST render as static (non-interactive) SSR — the auth pages
/// (<c>/login</c>, <c>/register</c>, <c>/logout</c>), whose antiforgery-tokened form posts only work
/// outside a Blazor circuit.
/// </summary>
/// <remarks>
/// This is the .NET 8 stand-in for .NET 9's <c>[ExcludeFromInteractiveRouting]</c> (which doesn't exist
/// in this TFM). <see cref="App"/> reads the matched page's component type from endpoint metadata
/// (<c>ComponentTypeMetadata</c>) and renders any page carrying this attribute statically. Because the
/// signal is the routed component type — resolved by Blazor's own router — it is immune to the request
/// path being rewritten by a reverse proxy (Caddy), unlike the old <c>HttpContext.Request.Path</c> check.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class StaticSsrPageAttribute : Attribute;
