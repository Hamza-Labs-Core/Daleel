namespace Daleel.Web.Data;

/// <summary>
/// A registered B2B API client organisation (spec 2026-07-19-b2b-api-design: the "Application").
/// There are no "users" on the API side — the Application is the account: it holds the keys, the
/// credit ledger and (later) the monitors/webhooks. A person only appears as the optional
/// <see cref="OwnerUserId"/> — the developer's portal login. Named <c>ApiApplication</c> in code
/// (the bare word "Application" is too collision-prone) but it is the spec's Application entity.
/// </summary>
public sealed class ApiApplication
{
    public const string StatusPending = "pending";
    public const string StatusActive = "active";
    public const string StatusSuspended = "suspended";

    public int Id { get; set; }

    /// <summary>Display name, e.g. "acme-prod". One owner login can own several applications.</summary>
    public string Name { get; set; } = string.Empty;

    public string ContactEmail { get; set; } = string.Empty;

    /// <summary>FK to Identity (AspNetUsers) — the developer's portal login. Null when the
    /// application was created by an admin before any owner registered.</summary>
    public string? OwnerUserId { get; set; }

    public int ApiPlanId { get; set; }
    public ApiPlan? Plan { get; set; }

    /// <summary>Lifecycle: pending → active → suspended. Only active applications may call the API.</summary>
    public string Status { get; set; } = StatusPending;

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// An issued API key. The full key (<c>dlk_live_&lt;43 url-safe chars&gt;</c>) is shown exactly once at
/// issue time and never stored — only its SHA-256 hex <see cref="Hash"/> (the lookup key) and a short
/// display <see cref="Prefix"/> survive. Sent as <c>Authorization: Bearer dlk_live_…</c>.
/// </summary>
public sealed class ApiKey
{
    public int Id { get; set; }

    public int ApplicationId { get; set; }
    public ApiApplication? Application { get; set; }

    /// <summary>SHA-256 hex (lower-case, 64 chars) of the FULL key string. Unique — the auth lookup key.</summary>
    public string Hash { get; set; } = string.Empty;

    /// <summary>Display fragment so an admin/owner can tell keys apart ("dlk_live_AbCd1234…").
    /// Never enough to reconstruct the key.</summary>
    public string Prefix { get; set; } = string.Empty;

    /// <summary>Comma-separated scope list, e.g. "items:read,stores:read,brands:read".</summary>
    public string Scopes { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Set when the key is revoked; a revoked key authenticates nothing (401) but the row
    /// stays for the audit trail.</summary>
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>Stamped (best-effort) on each authenticated call — "is this key still in use?".</summary>
    public DateTimeOffset? LastUsedAt { get; set; }

    /// <summary>Splits <see cref="Scopes"/> into its individual scope tokens.</summary>
    public IReadOnlyList<string> ScopeList() =>
        Scopes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>
/// A B2B API plan — deliberately NOT <see cref="SubscriptionPlan"/> (that one is the consumer
/// product's; different currency, different price points). Seeded via HasData (Trial/Starter/Growth)
/// and edited on /admin/api, never on /admin/plans.
/// </summary>
public sealed class ApiPlan
{
    // Fixed ids for the seeded tiers (HasData needs stable keys).
    public const int TrialId = 1;
    public const int StarterId = 2;
    public const int GrowthId = 3;

    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>B2B credits granted per month (the org-level pool every API action debits).</summary>
    public int MonthlyApiCredits { get; set; }

    /// <summary>Plan cap on subscribed store monitors (an abuse bound — the SPEND is all credits).</summary>
    public int MaxMonitoredStores { get; set; }

    public bool WebhooksEnabled { get; set; }

    public decimal MonthlyPriceUsd { get; set; }
}

/// <summary>
/// One movement on an application's B2B credit balance: positive = grant (period grant, top-up,
/// admin adjustment), negative = debit (per-call metering, monitor subscription). The balance IS
/// <c>SUM(Delta)</c> — append-only, no separate balance column to drift out of sync.
/// </summary>
public sealed class ApiCreditLedger
{
    public long Id { get; set; }

    public int ApplicationId { get; set; }
    public ApiApplication? Application { get; set; }

    /// <summary>Credit movement: +grant / -debit.</summary>
    public long Delta { get; set; }

    /// <summary>Why, e.g. "grant.admin", "items.list", "items.get". Endpoint name for per-call debits.</summary>
    public string Reason { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
}
