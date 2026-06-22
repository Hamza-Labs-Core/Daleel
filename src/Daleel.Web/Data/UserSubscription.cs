using System.ComponentModel.DataAnnotations;

namespace Daleel.Web.Data;

/// <summary>Links a user to their current plan. Stripe fields are reserved for later.</summary>
public sealed class UserSubscription
{
    public int Id { get; set; }

    [Required]
    public string UserId { get; set; } = string.Empty;

    public int PlanId { get; set; }
    public SubscriptionPlan? Plan { get; set; }

    /// <summary>active / cancelled / expired.</summary>
    public string Status { get; set; } = "active";

    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>Reserved for Stripe integration; null until billing is wired up.</summary>
    public string? StripeSubscriptionId { get; set; }
}

/// <summary>
/// Per-user usage counter for the current billing period. The authoritative limit lives on the
/// user's plan; <see cref="QuotaLimit"/> caches the resolved value for display.
/// </summary>
public sealed class UserQuota
{
    public int Id { get; set; }

    [Required]
    public string UserId { get; set; } = string.Empty;

    public int SearchesUsed { get; set; }

    /// <summary>Resolved monthly limit (null = unlimited), refreshed from the plan on each period.</summary>
    public int? QuotaLimit { get; set; }

    public DateTimeOffset PeriodStart { get; set; }
    public DateTimeOffset PeriodEnd { get; set; }
}
