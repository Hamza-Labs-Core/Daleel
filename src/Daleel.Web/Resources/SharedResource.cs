namespace Daleel.Web.Resources;

/// <summary>
/// Marker type for the shared UI string catalogue. Components inject
/// <c>IStringLocalizer&lt;SharedResource&gt;</c> and look strings up by key; translations live in
/// <c>Resources/SharedResource.resx</c> (English, default) and <c>SharedResource.ar.resx</c> (Arabic).
/// </summary>
public sealed class SharedResource
{
}
