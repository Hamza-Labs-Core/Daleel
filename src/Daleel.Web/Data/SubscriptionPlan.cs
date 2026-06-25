using System.ComponentModel.DataAnnotations;
using System.Text.Json;

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

    /// <summary>
    /// JSON array of feature bullet strings shown on the pricing page. This is the storage format only —
    /// the admin UI edits the decoded list via <see cref="GetFeatures"/>/<see cref="SetFeatures"/> and never
    /// touches the raw JSON.
    /// </summary>
    public string FeaturesJson { get; set; } = "[]";

    /// <summary>Decodes <see cref="FeaturesJson"/> into a list of feature strings. Returns an empty list for
    /// null/blank or malformed JSON, so a bad column value never breaks the admin screen.</summary>
    public List<string> GetFeatures()
    {
        if (string.IsNullOrWhiteSpace(FeaturesJson))
        {
            return new List<string>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(FeaturesJson) ?? new List<string>();
        }
        catch (JsonException)
        {
            return new List<string>();
        }
    }

    /// <summary>Serializes feature strings back into <see cref="FeaturesJson"/>, trimming each entry and
    /// dropping blanks so the stored array stays clean.</summary>
    public void SetFeatures(IEnumerable<string> features) =>
        FeaturesJson = JsonSerializer.Serialize(
            features.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f.Trim()).ToList());

    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }

    /// <summary>True when this plan grants unlimited searches.</summary>
    public bool IsUnlimited => SearchesPerMonth is null;

    // Stable ids so seeding and the default-plan lookup are deterministic.
    public const int BasicId = 1;
    public const int ProId = 2;
    public const int UnlimitedId = 3;
}
