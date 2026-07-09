using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Daleel.Web.Data;

/// <summary>
/// The VPS token authority: mints, stores (encrypted), rotates and serves the credentials that
/// authenticate this app to its Cloudflare workers, plus admin-managed vendor API keys. This is the
/// dynamic replacement for static GitHub-secret bearers — tokens are generated here, pushed to the
/// workers by <c>CredentialRotationService</c>, and read back by the app's own outbound clients, so
/// both sides of every app↔worker edge always agree without a human ever handling a token.
/// </summary>
public interface ICredentialVault
{
    /// <summary>Decrypted current value, or null when absent/revoked. DB-backed (always fresh).</summary>
    Task<string?> GetAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// SYNC read from the in-memory snapshot (refreshed on every write and periodically) — for hot
    /// paths like key resolution that cannot await. Falls back to null when the snapshot hasn't
    /// loaded yet; callers then use their environment fallback.
    /// </summary>
    string? TryGetCached(string name);

    /// <summary>Returns the existing credential value, or mints + persists a new one.</summary>
    Task<string> GetOrMintAsync(string name, string kind, CancellationToken ct = default);

    /// <summary>
    /// Mints a NEW value, demoting the current one to the previous slot (the rotation grace window).
    /// Returns (newValue, previousValue) so the caller can push both to the worker.
    /// </summary>
    Task<(string Current, string? Previous)> RotateAsync(string name, CancellationToken ct = default);

    /// <summary>Stores/overwrites a vendor key from the admin UI.</summary>
    Task SetAsync(string name, string value, string kind, string? notes = null, CancellationToken ct = default);

    /// <summary>Marks a bearer as successfully pushed to its worker.</summary>
    Task MarkPushedAsync(string name, CancellationToken ct = default);

    /// <summary>Metadata for the admin page (never the values).</summary>
    Task<IReadOnlyList<ServiceCredential>> ListAsync(CancellationToken ct = default);

    /// <summary>Reloads the sync snapshot from the database.</summary>
    Task RefreshSnapshotAsync(CancellationToken ct = default);
}

public sealed class CredentialVault : ICredentialVault
{
    /// <summary>Data-Protection purpose string — changing it orphans every stored value.</summary>
    private const string Purpose = "Daleel.ServiceCredentials.v1";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDataProtector _protector;
    private readonly ILogger<CredentialVault> _logger;
    private readonly ConcurrentDictionary<string, string> _snapshot = new(StringComparer.OrdinalIgnoreCase);

    public CredentialVault(
        IServiceScopeFactory scopeFactory, IDataProtectionProvider dataProtection,
        ILogger<CredentialVault> logger)
    {
        _scopeFactory = scopeFactory;
        _protector = dataProtection.CreateProtector(Purpose);
        _logger = logger;
    }

    /// <summary>256-bit CSPRNG token, hex-encoded — the same shape `openssl rand -hex 32` produces.</summary>
    private static string Mint() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    public async Task<string?> GetAsync(string name, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DaleelDbContext>();
        var row = await db.ServiceCredentials.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Name == name && !c.Revoked, ct).ConfigureAwait(false);
        return row is null ? null : Unprotect(row.EncryptedValue, name);
    }

    public string? TryGetCached(string name) =>
        _snapshot.TryGetValue(name, out var value) ? value : null;

    public async Task<string> GetOrMintAsync(string name, string kind, CancellationToken ct = default)
    {
        if (await GetAsync(name, ct).ConfigureAwait(false) is { } existing)
        {
            return existing;
        }

        var value = Mint();
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DaleelDbContext>();
        db.ServiceCredentials.Add(new ServiceCredential
        {
            Name = name,
            Kind = kind,
            EncryptedValue = _protector.Protect(value),
            CreatedAt = DateTimeOffset.UtcNow,
            Notes = "minted"
        });
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // Unique-name race (two instances minting simultaneously): the row that won is the truth.
            if (await GetAsync(name, ct).ConfigureAwait(false) is { } winner)
            {
                return winner;
            }
            throw;
        }

        _snapshot[name] = value;
        _logger.LogInformation("Minted credential {Name} ({Kind})", name, kind);
        return value;
    }

    public async Task<(string Current, string? Previous)> RotateAsync(string name, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DaleelDbContext>();
        var row = await db.ServiceCredentials.FirstOrDefaultAsync(c => c.Name == name, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Credential '{name}' does not exist — mint it first.");

        var previous = Unprotect(row.EncryptedValue, name);
        var next = Mint();
        row.EncryptedPreviousValue = row.EncryptedValue;
        row.EncryptedValue = _protector.Protect(next);
        row.RotatedAt = DateTimeOffset.UtcNow;
        row.Revoked = false;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        _snapshot[name] = next;
        _logger.LogInformation("Rotated credential {Name}", name);
        return (next, previous);
    }

    public async Task SetAsync(
        string name, string value, string kind, string? notes = null, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DaleelDbContext>();
        var row = await db.ServiceCredentials.FirstOrDefaultAsync(c => c.Name == name, ct).ConfigureAwait(false);
        if (row is null)
        {
            row = new ServiceCredential { Name = name, CreatedAt = DateTimeOffset.UtcNow };
            db.ServiceCredentials.Add(row);
        }
        else
        {
            row.EncryptedPreviousValue = row.EncryptedValue;
            row.RotatedAt = DateTimeOffset.UtcNow;
        }

        row.Kind = kind;
        row.EncryptedValue = _protector.Protect(value);
        row.Revoked = false;
        row.Notes = notes;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        _snapshot[name] = value;
        _logger.LogInformation("Stored credential {Name} ({Kind})", name, kind);
    }

    public async Task MarkPushedAsync(string name, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DaleelDbContext>();
        var row = await db.ServiceCredentials.FirstOrDefaultAsync(c => c.Name == name, ct).ConfigureAwait(false);
        if (row is not null)
        {
            row.PushedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<ServiceCredential>> ListAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DaleelDbContext>();
        // Metadata only — the encrypted blobs come along but are useless without this app's
        // Data-Protection keys, and the admin UI never renders them.
        return await db.ServiceCredentials.AsNoTracking()
            .OrderBy(c => c.Kind).ThenBy(c => c.Name)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task RefreshSnapshotAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DaleelDbContext>();
        var rows = await db.ServiceCredentials.AsNoTracking()
            .Where(c => !c.Revoked)
            .ToListAsync(ct).ConfigureAwait(false);

        foreach (var row in rows)
        {
            if (Unprotect(row.EncryptedValue, row.Name) is { } value)
            {
                _snapshot[row.Name] = value;
            }
        }

        // Drop snapshot entries whose rows were revoked/deleted.
        foreach (var stale in _snapshot.Keys.Except(rows.Select(r => r.Name), StringComparer.OrdinalIgnoreCase).ToList())
        {
            _snapshot.TryRemove(stale, out _);
        }
    }

    /// <summary>Decrypts, tolerating an unreadable blob (e.g. rotated Data-Protection keys).</summary>
    private string? Unprotect(string encrypted, string name)
    {
        try
        {
            return _protector.Unprotect(encrypted);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Credential {Name} could not be decrypted (Data-Protection key change?) — treat as absent; " +
                "rotate it to re-mint", name);
            return null;
        }
    }
}
