using Daleel.Web.Data;

namespace Daleel.Web.Profiles;

/// <summary>
/// Researches a brand or store into a fully-built (but not-yet-persisted) profile entity, using
/// Context.dev to gather content and the LLM to synthesize it. Returns <c>null</c> when the
/// required keys (Context.dev + an LLM) aren't configured, letting callers degrade gracefully
/// rather than throw. The profile <em>services</em> own DB-first/staleness; this owns the call-out.
/// </summary>
public interface IProfileResearcher
{
    /// <summary>True when both Context.dev and an LLM key are resolvable (research can actually run).</summary>
    bool IsAvailable { get; }

    Task<Brand?> ResearchBrandAsync(string brandName, string? geo, CancellationToken ct = default);

    Task<Store?> ResearchStoreAsync(string storeName, string? geo, CancellationToken ct = default);
}
