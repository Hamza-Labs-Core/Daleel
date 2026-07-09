using Daleel.Web.Data;

namespace Daleel.Web.Profiles;

/// <summary>
/// Owns the brand-profile lifecycle: serve from the database, and only call Context.dev when the
/// saved profile is missing or stale. This is the piece that makes profiles "fetched once and
/// refreshed periodically" rather than re-researched on every search.
/// </summary>
public interface IBrandProfileService
{
    /// <summary>
    /// DB-first lookup: returns the saved profile, transparently researching + persisting it only
    /// when missing or older than the TTL. When research is unavailable (no keys) it returns the
    /// existing (possibly stale) profile, or null if none exists.
    /// </summary>
    Task<Brand?> GetOrCreateAsync(string brandName, string? geo = null, CancellationToken ct = default);

    /// <summary>Forces a fresh research pass and persists it (admin manual refresh). Null if unavailable.</summary>
    Task<Brand?> RefreshAsync(string brandName, string? geo = null, CancellationToken ct = default);

    /// <summary>Re-researches up to <paramref name="max"/> stale profiles. Returns how many were refreshed.</summary>
    Task<int> RefreshStaleAsync(int max, CancellationToken ct = default);
}

public sealed class BrandProfileService : IBrandProfileService
{
    private readonly IBrandRepository _repo;
    private readonly IProfileResearcher _researcher;
    private readonly ProfileOptions _options;

    public BrandProfileService(IBrandRepository repo, IProfileResearcher researcher, ProfileOptions options)
    {
        _repo = repo;
        _researcher = researcher;
        _options = options;
    }

    public async Task<Brand?> GetOrCreateAsync(string brandName, string? geo = null, CancellationToken ct = default)
    {
        var existing = await _repo.GetByNameAsync(brandName, ct).ConfigureAwait(false);
        if (existing is not null && !existing.IsStale(_options.Now(), _options.Ttl))
        {
            return existing;
        }

        // Missing or stale → research. Fall back to the existing (stale) profile if research can't run.
        // The stale row's website is still a real, previously-verified URL — seed research with it.
        return await TryResearchAndSaveAsync(brandName, geo, existing?.Website, ct).ConfigureAwait(false) ?? existing;
    }

    // A FORCED refresh passes no hint on purpose: the admin reaches for this button precisely when
    // the saved profile looks wrong, and seeding research with the saved (possibly wrong) website
    // would just re-confirm it — the researcher must re-establish the site from scratch.
    public Task<Brand?> RefreshAsync(string brandName, string? geo = null, CancellationToken ct = default) =>
        TryResearchAndSaveAsync(brandName, geo, siteUrlHint: null, ct);

    public async Task<int> RefreshStaleAsync(int max, CancellationToken ct = default)
    {
        var cutoff = _options.Now() - _options.Ttl;
        var stale = await _repo.ListStaleAsync(cutoff, max, ct).ConfigureAwait(false);

        var refreshed = 0;
        foreach (var brand in stale)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            if (await TryResearchAndSaveAsync(brand.Name, null, brand.Website, ct).ConfigureAwait(false) is not null)
            {
                refreshed++;
            }
        }

        return refreshed;
    }

    private async Task<Brand?> TryResearchAndSaveAsync(
        string brandName, string? geo, string? siteUrlHint, CancellationToken ct)
    {
        var researched = await _researcher.ResearchBrandAsync(brandName, geo, ct, siteUrlHint).ConfigureAwait(false);
        if (researched is null)
        {
            return null;
        }

        researched.LastRefreshed = _options.Now();
        return await _repo.UpsertAsync(researched, ct).ConfigureAwait(false);
    }
}
