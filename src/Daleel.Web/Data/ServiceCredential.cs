using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Daleel.Web.Data;

/// <summary>
/// One managed credential in the VPS token authority: a worker bearer this app MINTS and pushes to
/// its Cloudflare worker, or a vendor API key an admin stored. The value is encrypted at rest with
/// ASP.NET Data Protection — never stored or logged in the clear — and only ever handed to (a) the
/// worker's secret store via the Cloudflare API and (b) this app's own outbound clients.
/// </summary>
/// <remarks>
/// This is what replaces static GitHub-secret bearers: tokens are generated dynamically, rotated
/// with an <c>AUTH_TOKEN_PREVIOUS</c> grace window, and both sides of every app↔worker edge read
/// the same row. Environment variables remain only as a BOOTSTRAP fallback for first-run/migration.
/// </remarks>
public sealed class ServiceCredential
{
    public int Id { get; set; }

    /// <summary>
    /// Unique credential name. Worker bearers use <c>worker:{script-name}</c> (the script name is
    /// already environment-scoped, e.g. <c>daleel-scrape-worker-qa</c>); vendor keys use the env-var
    /// name they stand in for (e.g. <c>CONTEXT_DEV_API_KEY</c>).
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>"worker-bearer" (minted + pushed by this app) or "vendor-key" (admin-provided).</summary>
    public string Kind { get; set; } = ServiceCredentialKind.WorkerBearer;

    /// <summary>Data-Protection-encrypted secret value. Decrypted only in memory, never logged.</summary>
    public string EncryptedValue { get; set; } = string.Empty;

    /// <summary>
    /// The PREVIOUS value (encrypted), kept through one rotation so the worker can accept both while
    /// the new value propagates; null once a rotation completes cleanly or before the first one.
    /// </summary>
    public string? EncryptedPreviousValue { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RotatedAt { get; set; }

    /// <summary>Last time this credential was successfully pushed to its worker (bearers only).</summary>
    public DateTimeOffset? PushedAt { get; set; }

    public bool Revoked { get; set; }

    /// <summary>Free-text operator note ("created from admin", "imported from env", …).</summary>
    public string? Notes { get; set; }
}

public static class ServiceCredentialKind
{
    public const string WorkerBearer = "worker-bearer";
    public const string VendorKey = "vendor-key";
}

public sealed class ServiceCredentialConfiguration : IEntityTypeConfiguration<ServiceCredential>
{
    public void Configure(EntityTypeBuilder<ServiceCredential> builder)
    {
        builder.HasIndex(c => c.Name).IsUnique();
        builder.Property(c => c.Name).HasMaxLength(200);
        builder.Property(c => c.Kind).HasMaxLength(32);
        builder.Property(c => c.Notes).HasMaxLength(500);
    }
}
