namespace Daleel.Core.Models;

/// <summary>How a brand's local standing should be surfaced to the user.</summary>
public enum ReputationFlag
{
    /// <summary>Nothing notable / not enough signal.</summary>
    None,

    /// <summary>Well-regarded with a local presence — worth highlighting.</summary>
    StrongLocalPresence,

    /// <summary>No/limited local service or support — buy with caution.</summary>
    LimitedLocalSupport
}

/// <summary>
/// A brand's reputation <em>in a specific market</em>: how reliable it's considered, the
/// common praise and complaints, and — crucially for a buying decision — whether it has
/// local after-sales service, spare parts, and warranty support.
/// </summary>
/// <remarks>
/// A cheap product from a brand with no local service is often a bad deal, so the local
/// service signal is first-class here, not buried in prose.
/// </remarks>
public record BrandReputation
{
    public string Brand { get; init; } = string.Empty;

    /// <summary>Overall reputation 1–5, when assessable.</summary>
    public double? Score { get; init; }

    public IReadOnlyList<string> Pros { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Complaints { get; init; } = Array.Empty<string>();

    /// <summary>Whether the brand has local service centres / after-sales support in the market.</summary>
    public bool? HasLocalService { get; init; }

    /// <summary>Short note on local service / spare-parts availability.</summary>
    public string? ServiceNote { get; init; }

    /// <summary>Warranty terms in the market, when known.</summary>
    public string? Warranty { get; init; }

    /// <summary>One- or two-sentence reputation summary.</summary>
    public string? Summary { get; init; }

    /// <summary>How to surface this brand: highlight strong presence, or warn about limited support.</summary>
    public ReputationFlag Flag
    {
        get
        {
            if (HasLocalService == false)
            {
                return ReputationFlag.LimitedLocalSupport;
            }

            if (HasLocalService == true && Score is >= 4.0)
            {
                return ReputationFlag.StrongLocalPresence;
            }

            return ReputationFlag.None;
        }
    }

    /// <summary>True when there's enough signal to show anything at all.</summary>
    public bool HasSignal =>
        Score is not null || Pros.Count > 0 || Complaints.Count > 0 ||
        HasLocalService is not null || !string.IsNullOrWhiteSpace(Summary);
}
