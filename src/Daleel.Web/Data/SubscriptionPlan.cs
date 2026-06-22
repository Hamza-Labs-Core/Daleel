using System.ComponentModel.DataAnnotations;

namespace Daleel.Web.Data;

/// <summary>
/// A subscription tier. Admin-configurable; the quota system reads <see cref="SearchesPerMonth"/>
/// from the user's active plan rather than any hardcoded number.
/// </summary>
public sealed class SubscriptionPlan
{
    public int Id { get; set; }

    [Required]
    public string Name { get; set; } = string.Empty;

    /// <summary>Monthly search allowance, or null for unlimited.</summary>
    public int? SearchesPerMonth { get; set; }

    public decimal PriceMonthly { get; set; }
    public decimal? PriceYearly { get; set; }

    /// <summary>JSON array of feature bullet strings shown on the pricing page.</summary>
    public string FeaturesJson { get; set; } = "[]";

    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }

    /// <summary>True when this plan grants unlimited searches.</summary>
    public bool IsUnlimited => SearchesPerMonth is null;

    // Stable ids so seeding and the default-plan lookup are deterministic.
    public const int BasicId = 1;
    public const int ProId = 2;
    public const int UnlimitedId = 3;
}
