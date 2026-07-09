using Daleel.Web.Data;

namespace Daleel.Web.Profiles;

/// <summary>
/// Researches a brand or store into a fully-built (but not-yet-persisted) profile entity, using
/// Context.dev to gather content and the LLM to synthesize it. Returns <c>null</c> when the
/// required keys (Context.dev + an LLM) aren't configured, letting callers degrade gracefully
/// rather than throw. The profile <em>services</em> own DB-first/staleness; this owns the call-out.
/// </summary>
/// <remarks>
/// <paramref name="siteUrlHint"/> on both methods is a REAL site URL the caller already knows —
/// a previously saved <c>Website</c>, or an actor-verified discovery from the calling workflow.
/// When absent, the researcher runs its own site discovery (Places for stores, the site-discovery
/// actor for both) and, if nothing real is found, skips the paid provider lookups entirely.
/// Hostnames are never fabricated from the entity's display name.
/// </remarks>
public interface IProfileResearcher
{
    /// <summary>True when both Context.dev and an LLM key are resolvable (research can actually run).</summary>
    bool IsAvailable { get; }

    Task<Brand?> ResearchBrandAsync(
        string brandName, string? geo, CancellationToken ct = default, string? siteUrlHint = null);

    Task<Store?> ResearchStoreAsync(
        string storeName, string? geo, CancellationToken ct = default, string? siteUrlHint = null);
}
